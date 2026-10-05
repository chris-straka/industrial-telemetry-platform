import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import type { TelemetryEvent } from '../../models/telemetry';

export interface ChartSeries {
  equipmentId: string;
  /** [event-time millis, temperature] pairs in arrival order. */
  points: Array<[number, number]>;
}

/** One series per machine. A single series over a mixed feed draws a line that
 * jumps between devices, which looks like noise the sensors never produced. */
export function groupSeries(data: TelemetryEvent[]): ChartSeries[] {
  const byEquipment = new Map<string, Array<[number, number]>>();
  for (const reading of data) {
    const points = byEquipment.get(reading.EquipmentId) ?? [];
    points.push([Date.parse(reading.OccurredAt), reading.EngineTemperature]);
    byEquipment.set(reading.EquipmentId, points);
  }
  return [...byEquipment.entries()].map(([equipmentId, points]) => ({ equipmentId, points }));
}

const WIDTH = 640;
const HEIGHT = 240;
const PAD = 28;

export interface ChartGeometry {
  paths: Array<{ equipmentId: string; d: string; color: string }>;
  minTemp: number;
  maxTemp: number;
}

const PALETTE = ['#1f77b4', '#d62728', '#2ca02c', '#9467bd', '#ff7f0e', '#17becf'];

/** Scales grouped series into SVG path data against event time, so readings
 * drained after an outage land where they happened rather than bunching up
 * at the moment the buffer flushed. Exported for unit tests. */
export function layoutChart(series: ChartSeries[]): ChartGeometry | null {
  const all = series.flatMap((s) => s.points);
  if (all.length === 0) return null;
  const times = all.map(([t]) => t);
  const temps = all.map(([, y]) => y);
  let minT = Math.min(...times);
  let maxT = Math.max(...times);
  let minTemp = Math.min(...temps);
  let maxTemp = Math.max(...temps);
  if (minT === maxT) {
    minT -= 1_000;
    maxT += 1_000;
  }
  if (minTemp === maxTemp) {
    minTemp -= 1;
    maxTemp += 1;
  }
  const x = (t: number): number => PAD + ((t - minT) / (maxT - minT)) * (WIDTH - 2 * PAD);
  const y = (v: number): number =>
    HEIGHT - PAD - ((v - minTemp) / (maxTemp - minTemp)) * (HEIGHT - 2 * PAD);
  const paths = series.map((s, i) => ({
    equipmentId: s.equipmentId,
    color: PALETTE[i % PALETTE.length],
    d: s.points.map(([t, v], j) => `${j === 0 ? 'M' : 'L'}${x(t).toFixed(1)},${y(v).toFixed(1)}`).join(' '),
  }));
  return { paths, minTemp, maxTemp };
}

@Component({
  selector: 'app-telemetry-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (geometry() === null) {
      <p class="empty">Waiting for telemetry…</p>
    } @else {
      <figure class="chart">
        <figcaption>Live Engine Temperatures</figcaption>
        <svg viewBox="0 0 640 240" role="img" aria-label="Live engine temperatures">
          @for (p of geometry()!.paths; track p.equipmentId) {
            <path [attr.d]="p.d" fill="none" [attr.stroke]="p.color" stroke-width="2" />
          }
        </svg>
        <ul class="legend">
          @for (p of geometry()!.paths; track p.equipmentId) {
            <li><span class="swatch" [style.background]="p.color"></span>{{ p.equipmentId }}</li>
          }
        </ul>
        <p class="range">
          {{ geometry()!.minTemp.toFixed(1) }} °C – {{ geometry()!.maxTemp.toFixed(1) }} °C
        </p>
      </figure>
    }
  `,
  styles: [
    `
      .chart {
        margin: 0;
      }
      figcaption {
        font-weight: 600;
        margin-bottom: 8px;
      }
      svg {
        width: 100%;
        height: auto;
        background: var(--mat-sys-surface-container-low, #f7f7f7);
        border-radius: 8px;
      }
      .legend {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        list-style: none;
        margin: 8px 0 0;
        padding: 0;
        font-size: 0.85rem;
      }
      .swatch {
        display: inline-block;
        width: 12px;
        height: 12px;
        border-radius: 3px;
        margin-right: 4px;
      }
      .range {
        font-size: 0.8rem;
        opacity: 0.75;
        margin: 4px 0 0;
      }
    `,
  ],
})
export class TelemetryChartComponent {
  private readonly dataSignal = signal<TelemetryEvent[]>([]);

  @Input()
  set data(value: TelemetryEvent[]) {
    this.dataSignal.set(value);
  }

  readonly geometry = computed(() => layoutChart(groupSeries(this.dataSignal())));
}
