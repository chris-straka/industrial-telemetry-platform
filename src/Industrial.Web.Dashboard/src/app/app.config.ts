import { inject, provideAppInitializer, provideZoneChangeDetection } from '@angular/core';
import type { ApplicationConfig } from '@angular/core';
import { provideRouter, withHashLocation } from '@angular/router';
import { AppConfigService } from './services/app-config.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    // Single-view dashboard; hash routing keeps deep links working when the
    // static host serves only index.html without SPA rewrites.
    provideRouter([], withHashLocation()),
    // Resolve the API origin before the root component opens its SignalR connection.
    provideAppInitializer(() => inject(AppConfigService).load()),
  ],
};
