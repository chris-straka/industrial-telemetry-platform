import { Injectable } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import type {
  ConnectionStatus,
  TelemetryAlert,
  TelemetryEvent,
} from '../models/telemetry';
import { isTelemetryAlert, isTelemetryEvent } from '../models/telemetry';

export interface TelemetryConnection {
  /** Pushes live readings and alerts until the returned teardown runs. */
  events$: Observable<TelemetryEvent>;
  alerts$: Observable<TelemetryAlert>;
  status$: Observable<ConnectionStatus>;
  disconnect(): void;
}

/**
 * RxJS facade over the SignalR telemetry hub. The Web API relays the Kafka
 * topics verbatim: `telemetry_events` / `telemetry_alerts` arrive as JSON
 * strings, one Hub method call per Kafka record.
 *
 * Connection policy mirrors the old hook: unbounded reconnect with capped
 * backoff, because automatic reconnect only kicks in after the first
 * successful start and the dashboard must survive the Compose race where the
 * browser loads before Web.Api finishes starting.
 */
@Injectable({ providedIn: 'root' })
export class TelemetryService {
  connect(apiUrl: string): TelemetryConnection {
    const events = new Subject<TelemetryEvent>();
    const alerts = new Subject<TelemetryAlert>();
    const status = new BehaviorSubject<ConnectionStatus>('connecting');

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${apiUrl.replace(/\/$/, '')}/telemetryHub`, {
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
      })
      .configureLogging(LogLevel.Information)
      .withAutomaticReconnect({
        // The built-in default stops after four tries. A cloud outage can be
        // much longer, so cap the delay without ever giving up on an open
        // dashboard.
        nextRetryDelayInMilliseconds: ({ previousRetryCount }: { previousRetryCount: number }) =>
          Math.min(1_000 * 2 ** Math.min(previousRetryCount, 5), 30_000),
      })
      .build();

    connection.on('telemetry_events', (payload: string) => {
      const parsed = parse(payload, isTelemetryEvent);
      if (parsed) events.next(parsed);
    });

    connection.on('telemetry_alerts', (payload: string) => {
      const parsed = parse(payload, isTelemetryAlert);
      if (parsed) alerts.next(parsed);
    });

    connection.onreconnecting(() => status.next('reconnecting'));
    connection.onreconnected(() => status.next('connected'));

    // With an unbounded retry policy onclose only fires on stop(), but a
    // closed connection that nobody stopped means the retry state machine
    // died: report it and restart the loop rather than freezing on a dead
    // socket.
    connection.onclose(() => {
      if (disposed) return;
      status.next('connecting');
      void start();
    });

    let disposed = false;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let retryDelayMs = 1_000;

    // Automatic reconnect starts only after one successful connection. This
    // loop covers the common Compose race where the browser loads before
    // Web.Api has finished starting.
    const start = async (): Promise<void> => {
      status.next('connecting');
      try {
        await connection.start();
        retryDelayMs = 1_000;
        status.next('connected');
      } catch (error) {
        if (disposed) return;
        console.error('SignalR initial connection failed; retrying', error);
        retryTimer = setTimeout(() => void start(), retryDelayMs);
        retryDelayMs = Math.min(retryDelayMs * 2, 30_000);
      }
    };

    void start();

    return {
      events$: events.asObservable(),
      alerts$: alerts.asObservable(),
      status$: status.asObservable(),
      disconnect: () => {
        disposed = true;
        if (retryTimer !== undefined) clearTimeout(retryTimer);
        void connection.stop();
      },
    };
  }
}

// A throw inside a SignalR handler kills the whole connection, so one bad
// payload would take the dashboard down until it reconnects.
function parse<T>(payload: string, isExpected: (value: unknown) => value is T): T | null {
  try {
    const value: unknown = JSON.parse(payload);
    if (isExpected(value)) return value;
    console.error('Discarding payload with the wrong telemetry shape', value);
    return null;
  } catch {
    console.error('Discarding unreadable payload', payload);
    return null;
  }
}
