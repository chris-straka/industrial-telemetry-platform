import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppConfigService } from './app-config.service';

function stubFetch(handler: () => Promise<unknown>): void {
  vi.stubGlobal('fetch', vi.fn(handler) as unknown as typeof fetch);
}

afterEach(() => {
  vi.unstubAllGlobals();
  delete (globalThis as Record<string, unknown>)['__INDUSTRIAL_API_URL__'];
});

describe('AppConfigService', () => {
  it('prefers the inline global override when present', async () => {
    (globalThis as Record<string, unknown>)['__INDUSTRIAL_API_URL__'] = 'http://api:5090/';
    stubFetch(() => Promise.reject(new Error('must not be called')));
    const config = new AppConfigService();
    await config.load();
    expect(config.apiUrl).toBe('http://api:5090');
  });

  it('reads the runtime config file and trims slashes', async () => {
    stubFetch(() =>
      Promise.resolve({ ok: true, json: () => Promise.resolve({ apiUrl: 'http://localhost:5090/' }) }),
    );
    const config = new AppConfigService();
    await config.load();
    expect(config.apiUrl).toBe('http://localhost:5090');
  });

  it('falls back to the page origin when the config file is missing', async () => {
    stubFetch(() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) }));
    const config = new AppConfigService();
    await config.load();
    expect(config.apiUrl).toBe(window.location.origin);
  });

  it('falls back to the page origin when the fetch itself fails', async () => {
    stubFetch(() => Promise.reject(new Error('offline')));
    const config = new AppConfigService();
    await config.load();
    expect(config.apiUrl).toBe(window.location.origin);
  });

  it('ignores a config file with the wrong shape', async () => {
    stubFetch(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ apiUrl: 42 }) }));
    const config = new AppConfigService();
    await config.load();
    expect(config.apiUrl).toBe(window.location.origin);
  });
});
