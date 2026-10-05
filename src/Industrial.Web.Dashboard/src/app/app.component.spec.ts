import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { AppComponent } from './app.component';
import type { ConnectionStatus, TelemetryAlert, TelemetryEvent } from './models/telemetry';
import { AppConfigService } from './services/app-config.service';
import { TelemetryService } from './services/telemetry.service';

function makeEvent(id: string, equipment = 'EQ-1', sequence = 1): TelemetryEvent {
  return {
    MessageId: id,
    EquipmentId: equipment,
    SequenceNumber: sequence,
    OccurredAt: new Date(Date.UTC(2026, 0, 1, 0, 0, sequence % 60)).toISOString(),
    ReceivedAt: new Date(Date.UTC(2026, 0, 1, 0, 0, sequence % 60)).toISOString(),
    EngineTemperature: 90,
    OilPressure: 40,
  };
}

function makeAlert(id: string, equipment = 'EQ-1'): TelemetryAlert {
  return {
    MessageId: id,
    EquipmentId: equipment,
    OccurredAt: '2026-09-04T00:00:00Z',
    EngineTemperature: 245,
    Diagnostics: `advice-${id}`,
  };
}

describe('AppComponent', () => {
  async function setup() {
    TestBed.resetTestingModule();
    const events$ = new Subject<TelemetryEvent>();
    const alerts$ = new Subject<TelemetryAlert>();
    const status$ = new BehaviorSubject<ConnectionStatus>('connecting');
    const disconnect = vi.fn();
    const connect = vi.fn(() => ({ events$, alerts$, status$, disconnect }));
    TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        { provide: TelemetryService, useValue: { connect } },
        { provide: AppConfigService, useValue: { apiUrl: 'http://localhost:5090' } },
      ],
    });
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, component: fixture.componentInstance, events$, alerts$, status$, disconnect, connect };
  }

  it('connects with the configured API origin', async () => {
    const { connect, fixture } = await setup();
    expect(connect).toHaveBeenCalledWith('http://localhost:5090');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Industrial Control Panel');
    fixture.destroy();
  });

  it('accumulates events, dedupes replays, and caps the buffer at 200', async () => {
    const { component, events$, fixture } = await setup();
    events$.next(makeEvent('a'));
    events$.next(makeEvent('a'));
    expect(component.events()).toHaveLength(1);

    for (let i = 0; i < 205; i++) {
      events$.next(makeEvent(`id-${i}`, 'EQ-1', i + 1));
    }
    expect(component.events()).toHaveLength(200);
    // The window keeps the newest points, not the first ones.
    expect(component.events()[199].MessageId).toBe('id-204');
    fixture.destroy();
  });

  it('keeps the newest 10 alerts newest-first', async () => {
    const { component, alerts$, fixture } = await setup();
    for (let i = 0; i < 12; i++) {
      alerts$.next(makeAlert(`alert-${i}`));
    }
    const alerts = component.alerts();
    expect(alerts).toHaveLength(10);
    expect(alerts[0].MessageId).toBe('alert-11');
    expect(alerts[9].MessageId).toBe('alert-2');
    fixture.destroy();
  });

  it('hides chart lines for unticked equipment but keeps its alerts', async () => {
    const { component, events$, alerts$, fixture } = await setup();
    events$.next(makeEvent('e1', 'EQ-1'));
    events$.next(makeEvent('e2', 'EQ-2'));
    alerts$.next(makeAlert('a1', 'EQ-2'));
    expect(component.visibleEvents()).toHaveLength(2);

    component.toggleEquipment('EQ-2');
    expect(component.visibleEvents().map((e) => e.EquipmentId)).toEqual(['EQ-1']);
    expect(component.alerts().map((a) => a.EquipmentId)).toEqual(['EQ-2']);

    component.toggleEquipment('EQ-2');
    expect(component.visibleEvents()).toHaveLength(2);
    fixture.destroy();
  });

  it('renders the chart, fleet, alerts, and live status', async () => {
    const { events$, alerts$, status$, fixture } = await setup();
    events$.next(makeEvent('e1', 'EQ-1'));
    alerts$.next(makeAlert('a1', 'EQ-1'));
    status$.next('connected');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('app-telemetry-chart svg path')).toHaveLength(1);
    expect(el.textContent).toContain('EQ-1');
    expect(el.textContent).toContain('advice-a1');
    expect(el.querySelector('[role="status"]')?.textContent).toContain('Live');
    fixture.destroy();
  });

  it('disconnects the stream when destroyed', async () => {
    const { disconnect, fixture } = await setup();
    fixture.destroy();
    expect(disconnect).toHaveBeenCalled();
  });
});
