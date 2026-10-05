import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  computed,
  effect,
  signal,
} from '@angular/core';
import { FormControl, FormGroup, FormRecord, ReactiveFormsModule } from '@angular/forms';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import type { EquipmentSummary } from '../../lib/equipment';

export const EQUIPMENT_COLUMNS = ['show', 'equipment', 'points', 'lastTemp', 'gaps'] as const;

/**
 * Fleet table. Visibility toggles hide chart lines only: alerts stay global
 * so unticking a noisy device can never silently hide its anomalies.
 *
 * The filter box and the per-row checkboxes are a reactive form; toggling a
 * row emits the equipment id and the parent owns the hidden set.
 */
@Component({
  selector: 'app-equipment-panel',
  standalone: true,
  imports: [MatCheckboxModule, MatFormFieldModule, MatInputModule, MatTableModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (summariesSignal().length === 0) {
      <p class="empty">No equipment seen yet.</p>
    } @else {
      <form [formGroup]="form">
        <mat-form-field appearance="outline" class="filter">
          <mat-label>Filter equipment</mat-label>
          <input matInput formControlName="filter" placeholder="EQ-" aria-label="Filter equipment" />
        </mat-form-field>
        <table mat-table [dataSource]="rows()" formGroupName="shown" class="equipment">
          <caption>
            Fleet
          </caption>
          <ng-container matColumnDef="show">
            <th mat-header-cell *matHeaderCellDef>Show</th>
            <td mat-cell *matCellDef="let row">
              <mat-checkbox
                [formControlName]="row.equipmentId"
                [attr.aria-label]="'Show ' + row.equipmentId"
              />
            </td>
          </ng-container>
          <ng-container matColumnDef="equipment">
            <th mat-header-cell *matHeaderCellDef>Equipment</th>
            <td mat-cell *matCellDef="let row">{{ row.equipmentId }}</td>
          </ng-container>
          <ng-container matColumnDef="points">
            <th mat-header-cell *matHeaderCellDef>Points</th>
            <td mat-cell *matCellDef="let row">{{ row.count }}</td>
          </ng-container>
          <ng-container matColumnDef="lastTemp">
            <th mat-header-cell *matHeaderCellDef>Last °C</th>
            <td mat-cell *matCellDef="let row">{{ row.lastTemperature.toFixed(1) }}</td>
          </ng-container>
          <ng-container matColumnDef="gaps">
            <th mat-header-cell *matHeaderCellDef>Seq gaps</th>
            <td mat-cell *matCellDef="let row">
              {{ row.gaps > 0 ? row.gaps + ' gap' + (row.gaps === 1 ? '' : 's') : '—' }}
            </td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr
            mat-row
            *matRowDef="let row; columns: columns"
            [class.muted]="hiddenSignal().has(row.equipmentId)"
          ></tr>
        </table>
      </form>
    }
  `,
  styles: [
    `
      .filter {
        width: 100%;
        max-width: 320px;
      }
      table.equipment {
        width: 100%;
      }
      tr.muted {
        opacity: 0.55;
      }
    `,
  ],
})
export class EquipmentPanelComponent {
  readonly summariesSignal = signal<EquipmentSummary[]>([]);
  readonly hiddenSignal = signal<Set<string>>(new Set());

  @Input()
  set summaries(value: EquipmentSummary[]) {
    this.summariesSignal.set(value);
  }

  @Input()
  set hidden(value: Set<string>) {
    this.hiddenSignal.set(value);
  }

  @Output()
  readonly toggle = new EventEmitter<string>();

  readonly columns = [...EQUIPMENT_COLUMNS];

  readonly form = new FormGroup({
    filter: new FormControl('', { nonNullable: true }),
    shown: new FormRecord<FormControl<boolean>>({}),
  });

  private readonly filterText = signal('');

  readonly rows = computed(() => {
    const needle = this.filterText().trim().toLowerCase();
    const all = this.summariesSignal();
    if (needle === '') return all;
    return all.filter((s) => s.equipmentId.toLowerCase().includes(needle));
  });

  constructor() {
    this.form.controls.filter.valueChanges.subscribe((value) => this.filterText.set(value));
    // Rebuild one checkbox control per device whenever the fleet changes,
    // preserving the parent-owned hidden set as the checked source of truth.
    effect(() => {
      const summaries = this.summariesSignal();
      const hidden = this.hiddenSignal();
      const shown = this.form.controls.shown;
      for (const id of Object.keys(shown.controls)) {
        if (!summaries.some((s) => s.equipmentId === id)) shown.removeControl(id);
      }
      for (const s of summaries) {
        const existing = shown.controls[s.equipmentId];
        const checked = !hidden.has(s.equipmentId);
        if (existing) {
          if (existing.value !== checked) existing.setValue(checked, { emitEvent: false });
        } else {
          const control = new FormControl<boolean>(checked, { nonNullable: true });
          control.valueChanges.subscribe(() => this.toggle.emit(s.equipmentId));
          shown.addControl(s.equipmentId, control);
        }
      }
    });
  }
}
