import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import { MatTableModule } from '@angular/material/table';
import type { TelemetryAlert } from '../../models/telemetry';

export const ALERT_COLUMNS = ['when', 'equipment', 'advice'] as const;

export interface AlertRow {
  id: string;
  when: string;
  equipment: string;
  temperature: number;
  diagnostics: string;
}

export function toAlertRows(alerts: TelemetryAlert[]): AlertRow[] {
  return alerts.map((alert) => ({
    id: alert.MessageId,
    when: new Date(alert.OccurredAt).toLocaleTimeString(),
    equipment: alert.EquipmentId,
    temperature: alert.EngineTemperature,
    diagnostics: alert.Diagnostics,
  }));
}

/** Global anomaly feed. Never filtered by the equipment visibility toggles. */
@Component({
  selector: 'app-alerts-list',
  standalone: true,
  imports: [MatTableModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <table mat-table [dataSource]="rows()" class="alerts">
      <caption>
        Anomaly Alerts
      </caption>
      <ng-container matColumnDef="when">
        <th mat-header-cell *matHeaderCellDef>When</th>
        <td mat-cell *matCellDef="let row">{{ row.when }}</td>
      </ng-container>
      <ng-container matColumnDef="equipment">
        <th mat-header-cell *matHeaderCellDef>Equipment</th>
        <td mat-cell *matCellDef="let row">{{ row.equipment }}</td>
      </ng-container>
      <ng-container matColumnDef="advice">
        <th mat-header-cell *matHeaderCellDef>AI Advice</th>
        <td mat-cell *matCellDef="let row">{{ row.diagnostics }}</td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="columns"></tr>
      <tr mat-row *matRowDef="let row; columns: columns"></tr>
      <tr class="mat-row" *matNoDataRow>
        <td class="mat-cell empty" [attr.colspan]="columns.length">No alerts yet.</td>
      </tr>
    </table>
  `,
  styles: [
    `
      table.alerts {
        width: 100%;
      }
      .empty {
        text-align: center;
        opacity: 0.7;
      }
    `,
  ],
})
export class AlertsListComponent {
  private readonly alertsSignal = signal<TelemetryAlert[]>([]);
  readonly columns = [...ALERT_COLUMNS];

  @Input()
  set alerts(value: TelemetryAlert[]) {
    this.alertsSignal.set(value);
  }

  readonly rows = computed(() => toAlertRows(this.alertsSignal()));
}
