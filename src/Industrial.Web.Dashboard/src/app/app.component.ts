import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { OnDestroy } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AlertsListComponent } from './components/alerts-list/alerts-list.component';
import { EquipmentPanelComponent } from './components/equipment-panel/equipment-panel.component';
import { StatusBadgeComponent } from './components/status-badge/status-badge.component';
import { TelemetryChartComponent } from './components/telemetry-chart/telemetry-chart.component';
import { summarizeEquipment } from './lib/equipment';
import { remember } from './lib/dedupe';
import type { TelemetryAlert, TelemetryEvent } from './models/telemetry';
import { AppConfigService } from './services/app-config.service';
import { TelemetryService } from './services/telemetry.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    AlertsListComponent,
    EquipmentPanelComponent,
    StatusBadgeComponent,
    TelemetryChartComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="dashboard">
      <header class="dashboard-header">
        <h1>Industrial Control Panel</h1>
        <app-status-badge [status]="status()" [lastMessageAt]="lastMessageAt()" />
      </header>
      <div class="grid">
        <section>
          <app-telemetry-chart [data]="visibleEvents()" />
          <app-equipment-panel
            [summaries]="summaries()"
            [hidden]="hiddenEquipment()"
            (toggle)="toggleEquipment($event)"
          />
        </section>
        <aside>
          <app-alerts-list [alerts]="alerts()" />
        </aside>
      </div>
    </div>
  `,
  styles: [
    `
      .dashboard {
        max-width: 1200px;
        margin: 0 auto;
        padding: 24px;
      }
      .dashboard-header {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 16px;
        flex-wrap: wrap;
        margin-bottom: 20px;
      }
      .dashboard-header h1 {
        margin: 0;
        font-size: 1.4rem;
      }
      .grid {
        display: grid;
        grid-template-columns: 2fr 1fr;
        gap: 24px;
        align-items: start;
      }
      @media (max-width: 900px) {
        .grid {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class AppComponent implements OnDestroy {
  private readonly telemetry = inject(TelemetryService);
  private readonly config = inject(AppConfigService);

  readonly events = signal<TelemetryEvent[]>([]);
  readonly alerts = signal<TelemetryAlert[]>([]);
  readonly lastMessageAt = signal<number | null>(null);
  readonly hiddenEquipment = signal<Set<string>>(new Set());

  private readonly seenEventIds = new Set<string>();
  private readonly eventIdOrder: string[] = [];
  private readonly seenAlertIds = new Set<string>();
  private readonly alertIdOrder: string[] = [];

  private readonly connection = this.telemetry.connect(this.config.apiUrl);
  private readonly liveStatus = toSignal(this.connection.status$, { initialValue: 'connecting' as const });

  readonly status = computed(() => this.liveStatus());

  // The filter hides chart lines only. Alerts stay global so unticking a noisy
  // device can never silently hide its anomalies.
  readonly visibleEvents = computed(() => {
    const hidden = this.hiddenEquipment();
    const events = this.events();
    if (hidden.size === 0) return events;
    return events.filter((event) => !hidden.has(event.EquipmentId));
  });

  readonly summaries = computed(() => summarizeEquipment(this.events()));

  constructor() {
    this.connection.events$.subscribe((event) => {
      if (!remember(event.MessageId, this.seenEventIds, this.eventIdOrder, 2_000)) return;
      // Shared across every device, so this is ~50 points each at 4 devices.
      this.events.update((prev) => [...prev.slice(-199), event]);
      this.lastMessageAt.set(Date.now());
    });
    this.connection.alerts$.subscribe((alert) => {
      if (!remember(alert.MessageId, this.seenAlertIds, this.alertIdOrder, 500)) return;
      // Keep top 10.
      this.alerts.update((prev) => [alert, ...prev.slice(0, 9)]);
      this.lastMessageAt.set(Date.now());
    });
  }

  toggleEquipment(equipmentId: string): void {
    this.hiddenEquipment.update((prev) => {
      const next = new Set(prev);
      if (next.has(equipmentId)) next.delete(equipmentId);
      else next.add(equipmentId);
      return next;
    });
  }

  ngOnDestroy(): void {
    this.connection.disconnect();
  }
}
