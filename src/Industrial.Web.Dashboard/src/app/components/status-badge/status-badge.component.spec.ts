import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  StatusBadgeComponent,
  STALE_AFTER_MS,
} from './status-badge.component';

beforeEach(() => {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ imports: [StatusBadgeComponent] });
});

async function render(status: 'connecting' | 'connected' | 'reconnecting', lastMessageAt: number | null, now: number) {
  const fixture = TestBed.createComponent(StatusBadgeComponent);
  fixture.componentRef.setInput('status', status);
  fixture.componentRef.setInput('lastMessageAt', lastMessageAt);
  fixture.componentRef.setInput('now', now);
  fixture.detectChanges();
  await fixture.whenStable();
  return (fixture.nativeElement as HTMLElement).querySelector('[role="status"]')!;
}

describe('StatusBadgeComponent', () => {
  it('shows Live on a fresh connected socket', async () => {
    const badge = await render('connected', 5_000, 8_000);
    expect(badge.textContent).toContain('Live');
    expect(badge.className).toContain('status-live');
  });

  it('shows Stale with an age once quiet past the threshold', async () => {
    const badge = await render('connected', 0, STALE_AFTER_MS + 5_000);
    expect(badge.textContent).toContain('Stale · 15s');
    expect(badge.className).toContain('status-stale');
  });

  it('treats a connected socket with no messages yet as Stale', async () => {
    const badge = await render('connected', null, 1_000);
    expect(badge.textContent).toContain('Stale');
  });

  it('shows Reconnecting and Connecting transients', async () => {
    expect((await render('reconnecting', null, 0)).textContent).toContain('Reconnecting');
    expect((await render('connecting', null, 0)).textContent).toContain('Connecting');
  });
});
