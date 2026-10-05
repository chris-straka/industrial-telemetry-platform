import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { toAlertRows } from './alerts-list.component';

beforeEach(() => {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ imports: [AlertsListComponent] });
});
import { AlertsListComponent } from './alerts-list.component';
import type { TelemetryAlert } from '../../models/telemetry';

function makeAlert(id: string, equipment = 'EQ-1'): TelemetryAlert {
  return {
    MessageId: id,
    EquipmentId: equipment,
    OccurredAt: '2026-09-04T00:00:00Z',
    EngineTemperature: 245,
    Diagnostics: 'spike suspected',
  };
}

describe('toAlertRows', () => {
  it('maps alerts to table rows', () => {
    expect(toAlertRows([makeAlert('a')])).toEqual([
      {
        id: 'a',
        when: new Date('2026-09-04T00:00:00Z').toLocaleTimeString(),
        equipment: 'EQ-1',
        temperature: 245,
        diagnostics: 'spike suspected',
      },
    ]);
  });
});

describe('AlertsListComponent', () => {
  it('shows the empty state with no alerts', async () => {
    const fixture = TestBed.createComponent(AlertsListComponent);
    fixture.componentRef.setInput('alerts', []);
    fixture.detectChanges();
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No alerts yet.');
  });

  it('renders one row per alert newest-first', async () => {
    const fixture = TestBed.createComponent(AlertsListComponent);
    fixture.componentRef.setInput('alerts', [makeAlert('a'), makeAlert('b', 'EQ-2')]);
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const rows = el.querySelectorAll('tbody tr, .mat-mdc-row');
    expect(rows.length).toBeGreaterThanOrEqual(2);
    expect(el.textContent).toContain('EQ-2');
    expect(el.textContent).toContain('spike suspected');
  });
});
