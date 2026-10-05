import { Injectable } from '@angular/core';

export interface AppRuntimeConfig {
  /** API origin. Empty means same origin as the served dashboard. */
  apiUrl: string;
}

/**
 * Runtime configuration loaded before bootstrap. Production bakes the Docker
 * VITE_API_URL build arg into assets/app-config.json; development writes it
 * there from the environment via `npm run dev`. An absent or empty file
 * means the static site and the API share one origin.
 */
@Injectable({ providedIn: 'root' })
export class AppConfigService {
  private config: AppRuntimeConfig = { apiUrl: '' };

  async load(): Promise<void> {
    const override =
      typeof globalThis !== 'undefined' &&
      typeof (globalThis as Record<string, unknown>)['__INDUSTRIAL_API_URL__'] === 'string'
        ? ((globalThis as Record<string, unknown>)['__INDUSTRIAL_API_URL__'] as string).trim()
        : '';
    if (override !== '') {
      this.config = { apiUrl: override };
      return;
    }
    try {
      const response = await fetch('assets/app-config.json', { cache: 'no-store' });
      if (!response.ok) return;
      const body: unknown = await response.json();
      if (
        typeof body === 'object' &&
        body !== null &&
        typeof (body as Record<string, unknown>)['apiUrl'] === 'string'
      ) {
        this.config = {
          apiUrl: ((body as Record<string, unknown>)['apiUrl'] as string).trim(),
        };
      }
    } catch {
      // No config file: same-origin default below.
    }
  }

  get apiUrl(): string {
    if (this.config.apiUrl !== '') return this.config.apiUrl.replace(/\/$/, '');
    if (typeof window !== 'undefined' && window.location?.origin) {
      return window.location.origin;
    }
    return '';
  }
}
