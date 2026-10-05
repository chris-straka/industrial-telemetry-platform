import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { makeEvent } from '../../lib/equipment.spec';
import {
  TelemetryChartComponent,
  groupSeries,
  layoutChart,
} from './telemetry-chart.component';

beforeEach(() => {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ imports: [TelemetryChartComponent] });
});

describe('groupSeries', () => {
  it('keeps one series per machine in arrival order', () => {
    const series = groupSeries([
      makeEvent('EQ-1', 1, 90),
      makeEvent('EQ-2', 1, 80),
      makeEvent('EQ-1', 2, 91),
    ]);
    expect(series.map((s) => s.equipmentId)).toEqual(['EQ-1', 'EQ-2']);
    expect(series[0].points).toHaveLength(2);
    // Event time, not arrival time: the chart x-axis is OccurredAt.
    expect(series[0].points[0][0]).toBe(Date.parse('2026-01-01T00:00:01.000Z'));
    expect(series[0].points[0][1]).toBe(90);
  });
});

describe('layoutChart', () => {
  it('returns null for an empty feed', () => {
    expect(layoutChart([])).toBeNull();
  });

  it('maps a single point without dividing by zero', () => {
    const geometry = layoutChart(groupSeries([makeEvent('EQ-1', 1, 90)]));
    expect(geometry).not.toBeNull();
    expect(geometry!.paths).toHaveLength(1);
    expect(geometry!.paths[0].d).toMatch(/^M-?\d+\.\d+,-?\d+\.\d+$/);
  });

  it('scales two series into the viewBox', () => {
    const geometry = layoutChart(
      groupSeries([makeEvent('EQ-1', 1, 80), makeEvent('EQ-2', 2, 100)]),
    );
    expect(geometry!.paths).toHaveLength(2);
    expect(geometry!.minTemp).toBe(80);
    expect(geometry!.maxTemp).toBe(100);
  });
});

describe('TelemetryChartComponent', () => {
  it('waits for telemetry on an empty feed', async () => {
    const fixture = TestBed.createComponent(TelemetryChartComponent);
    fixture.componentRef.setInput('data', []);
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Waiting for telemetry');
    expect(el.querySelector('svg')).toBeNull();
  });

  it('draws one path per device', async () => {
    const fixture = TestBed.createComponent(TelemetryChartComponent);
    fixture.componentRef.setInput('data', [makeEvent('EQ-1', 1), makeEvent('EQ-2', 1)]);
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('svg path')).toHaveLength(2);
    expect(el.textContent).toContain('EQ-1');
    expect(el.textContent).toContain('EQ-2');
  });
});
