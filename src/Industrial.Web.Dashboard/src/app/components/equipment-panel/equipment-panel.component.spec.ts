import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { summarizeEquipment } from '../../lib/equipment';
import { makeEvent } from '../../lib/equipment.spec';
import { EquipmentPanelComponent } from './equipment-panel.component';

beforeEach(() => {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ imports: [EquipmentPanelComponent] });
});

function summaries() {
  return summarizeEquipment([
    makeEvent('EQ-1', 1, 90),
    makeEvent('EQ-1', 2, 95.5),
    makeEvent('EQ-2', 1, 80),
  ]);
}

describe('EquipmentPanelComponent', () => {
  it('shows the empty state before any device reports', async () => {
    const fixture = TestBed.createComponent(EquipmentPanelComponent);
    fixture.componentRef.setInput('summaries', []);
    fixture.componentRef.setInput('hidden', new Set<string>());
    fixture.detectChanges();
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'No equipment seen yet.',
    );
  });

  it('renders one Material row per device with counts and gaps', async () => {
    const fixture = TestBed.createComponent(EquipmentPanelComponent);
    fixture.componentRef.setInput('summaries', summaries());
    fixture.componentRef.setInput('hidden', new Set<string>());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('EQ-1');
    expect(el.textContent).toContain('95.5');
    expect(el.textContent).toContain('—');
  });

  it('emits the equipment id when its checkbox toggles', async () => {
    const fixture = TestBed.createComponent(EquipmentPanelComponent);
    fixture.componentRef.setInput('summaries', summaries());
    fixture.componentRef.setInput('hidden', new Set<string>());
    const toggled: string[] = [];
    fixture.componentInstance.toggle.subscribe((id) => toggled.push(id));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const checkbox = (fixture.nativeElement as HTMLElement).querySelector(
      'input[type="checkbox"]',
    ) as HTMLInputElement;
    expect(checkbox).not.toBeNull();
    checkbox.click();
    expect(toggled).toEqual(['EQ-1']);
  });

  it('filters rows through the reactive filter control', async () => {
    const fixture = TestBed.createComponent(EquipmentPanelComponent);
    fixture.componentRef.setInput('summaries', summaries());
    fixture.componentRef.setInput('hidden', new Set<string>());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const component = fixture.componentInstance;
    component.form.controls.filter.setValue('EQ-2');
    fixture.detectChanges();
    expect(component.rows().map((r) => r.equipmentId)).toEqual(['EQ-2']);
    const onSpy = vi.fn();
    component.toggle.subscribe(onSpy);
    expect(onSpy).not.toHaveBeenCalled();
  });
});
