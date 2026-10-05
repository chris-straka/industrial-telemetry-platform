import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TelemetryAlert, TelemetryEvent } from '../models/telemetry';
import { TelemetryService } from './telemetry.service';

// Everything the mock needs lives in vi.hoisted: the mock factory runs
// during import resolution, before any other top-level declaration.
const state = vi.hoisted(() => {
  class FakeConnection {
    url = '';
    handlers = new Map<string, (payload: string) => void>();
    reconnectingCb: (() => void) | undefined;
    reconnectedCb: (() => void) | undefined;
    closeCb: (() => void) | undefined;
    startImpl: () => Promise<void> = () => Promise.resolve();
    stop = vi.fn(() => Promise.resolve());
    on(method: string, cb: (payload: string) => void): void {
      this.handlers.set(method, cb);
    }
    onreconnecting(cb: () => void): void {
      this.reconnectingCb = cb;
    }
    onreconnected(cb: () => void): void {
      this.reconnectedCb = cb;
    }
    onclose(cb: () => void): void {
      this.closeCb = cb;
    }
    start(): Promise<void> {
      return this.startImpl();
    }
    emit(method: string, payload: string): void {
      this.handlers.get(method)?.(payload);
    }
  }

  class FakeBuilder {
    private url = '';
    withUrl(url: string): this {
      this.url = url;
      return this;
    }
    configureLogging(): this {
      return this;
    }
    withAutomaticReconnect(): this {
      return this;
    }
    build(): FakeConnection {
      const connection = new FakeConnection();
      connection.url = this.url;
      connections.push(connection);
      return connection;
    }
  }

  const connections: FakeConnection[] = [];
  return { connections, FakeBuilder };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: state.FakeBuilder,
  HttpTransportType: { WebSockets: 1 },
  LogLevel: { Information: 2 },
}));

type FakeConnection = (typeof state.connections)[number];

const event: TelemetryEvent = {
  MessageId: '11111111-1111-4111-8111-111111111111',
  EquipmentId: 'EQ-0',
  SequenceNumber: 1,
  OccurredAt: '2026-09-04T00:00:00Z',
  ReceivedAt: '2026-09-04T00:00:01Z',
  EngineTemperature: 80,
  OilPressure: 40,
};

const alert: TelemetryAlert = {
  MessageId: '22222222-2222-4222-8222-222222222222',
  EquipmentId: 'EQ-0',
  OccurredAt: '2026-09-04T00:00:00Z',
  EngineTemperature: 245,
  Diagnostics: 'spike',
};

function connection(): FakeConnection {
  expect(state.connections).toHaveLength(1);
  return state.connections[0];
}

async function flush(times = 5): Promise<void> {
  for (let i = 0; i < times; i++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

describe('TelemetryService', () => {
  beforeEach(() => {
    state.connections.length = 0;
    vi.restoreAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  it('opens the hub under the given API origin and reports connected', async () => {
    const service = new TelemetryService();
    const statuses: string[] = [];
    const link = service.connect('http://localhost:5090/');
    link.status$.subscribe((s) => statuses.push(s));
    await flush();

    expect(connection().url).toBe('http://localhost:5090/telemetryHub');
    expect(statuses).toContain('connected');
    link.disconnect();
  });

  it('streams parsed events and alerts as RxJS observables', async () => {
    const service = new TelemetryService();
    const link = service.connect('http://localhost:5090');
    const events: TelemetryEvent[] = [];
    const alerts: TelemetryAlert[] = [];
    link.events$.subscribe((e) => events.push(e));
    link.alerts$.subscribe((a) => alerts.push(a));
    await flush();

    connection().emit('telemetry_events', JSON.stringify(event));
    connection().emit('telemetry_alerts', JSON.stringify(alert));
    expect(events).toEqual([event]);
    expect(alerts).toEqual([alert]);
    link.disconnect();
  });

  it('discards bad payloads without killing the stream', async () => {
    const service = new TelemetryService();
    const link = service.connect('http://localhost:5090');
    const events: TelemetryEvent[] = [];
    link.events$.subscribe((e) => events.push(e));
    await flush();

    connection().emit('telemetry_events', 'not-json{{{');
    connection().emit('telemetry_events', JSON.stringify({ ...event, SequenceNumber: 0 }));
    expect(events).toEqual([]);
    connection().emit('telemetry_events', JSON.stringify(event));
    expect(events).toEqual([event]);
    expect(console.error).toHaveBeenCalled();
    link.disconnect();
  });

  it('mirrors reconnecting and reconnected hub transitions', async () => {
    const service = new TelemetryService();
    const link = service.connect('http://localhost:5090');
    const statuses: string[] = [];
    link.status$.subscribe((s) => statuses.push(s));
    await flush();

    connection().reconnectingCb?.();
    connection().reconnectedCb?.();
    expect(statuses).toContain('reconnecting');
    expect(statuses[statuses.length - 1]).toBe('connected');
    link.disconnect();
  });

  it('restarts a closed connection nobody stopped and stops on disconnect', async () => {
    const service = new TelemetryService();
    const link = service.connect('http://localhost:5090');
    await flush();
    const started = vi.fn(() => Promise.resolve());
    connection().startImpl = started;

    connection().closeCb?.();
    await flush();
    expect(started).toHaveBeenCalled();

    link.disconnect();
    expect(connection().stop).toHaveBeenCalled();
  });
});
