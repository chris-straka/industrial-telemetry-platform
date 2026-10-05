import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { interval, map } from 'rxjs';
import type { ConnectionStatus } from '../../models/telemetry';

// Readings arrive every couple of seconds per device in the demo fleet, so ten
// quiet seconds on a live socket means the pipeline behind it stopped moving.
export const STALE_AFTER_MS = 10_000;

@Component({
  selector: 'app-status-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="status status-{{ tone() }}" role="status">
      <span class="status-dot" aria-hidden="true"></span>{{ label() }}
    </p>
  `,
  styles: [
    `
      .status {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        margin: 0;
        padding: 4px 12px;
        border: 1px solid var(--mat-sys-outline-variant, #c4c6d0);
        border-radius: 999px;
        font-size: 0.85rem;
      }
      .status-dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: currentcolor;
      }
      .status-live {
        color: #1a7f37;
      }
      .status-stale {
        color: #b54708;
      }
      .status-busy {
        color: #9aa0a6;
      }
    `,
  ],
})
export class StatusBadgeComponent {
  // Signal-backed inputs: plain @Input setters feed writable signals so the
  // JIT TestBed path (which cannot see signal input() fields) stays green
  // while all derived state remains computed signals.
  private readonly statusSignal = signal<ConnectionStatus>('connecting');
  private readonly lastMessageSignal = signal<number | null>(null);
  private readonly nowOverride = signal<number | null>(null);

  @Input()
  set status(value: ConnectionStatus) {
    this.statusSignal.set(value);
  }

  @Input()
  set lastMessageAt(value: number | null) {
    this.lastMessageSignal.set(value);
  }

  /** Clock override for tests; defaults to a one-second tick. */
  @Input()
  set now(value: number | null) {
    this.nowOverride.set(value);
  }

  private readonly ticked = toSignal(interval(1_000).pipe(map(() => Date.now())), {
    initialValue: Date.now(),
  });

  private readonly clock = computed(() => this.nowOverride() ?? this.ticked());

  private readonly ageMs = computed(() => {
    const last = this.lastMessageSignal();
    const clock = this.clock();
    if (last === null || clock === null) return null;
    return Math.max(0, clock - last);
  });

  private readonly stale = computed(
    () =>
      this.statusSignal() === 'connected' &&
      (this.ageMs() === null || (this.ageMs() as number) > STALE_AFTER_MS),
  );

  readonly label = computed(() => {
    const status = this.statusSignal();
    if (status === 'connected' && !this.stale()) return 'Live';
    if (status === 'reconnecting') return 'Reconnecting…';
    if (status === 'connected') {
      const age = this.ageMs();
      return age === null ? 'Stale' : `Stale · ${Math.round(age / 1_000)}s`;
    }
    return 'Connecting…';
  });

  readonly tone = computed(() => {
    const label = this.label();
    if (label === 'Live') return 'live';
    if (label.startsWith('Stale')) return 'stale';
    return 'busy';
  });
}
