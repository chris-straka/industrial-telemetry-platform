# Industrial Web Dashboard (Angular)

Live telemetry wall for the IndustrialPlatform pipeline:
`sensor emulator -> edge gateway -> ingestion API -> Kafka -> diagnostics/Postgres -> SignalR dashboard`.

## Stack

- Angular 22 CLI (`@angular/build` application builder), standalone OnPush components, signals
  (`signal`/`computed`/`toSignal`) for view state
- RxJS `TelemetryService` over the SignalR `/telemetryHub` stream
  (`telemetry_events` / `telemetry_alerts`, JSON string per Kafka record)
- Angular Material tables for the fleet and anomaly feeds
- Reactive forms for the equipment visibility filter
- `vitest` specs colocated with each source file (`*.spec.ts`)

## Develop

```sh
npm ci
npm run dev        # ng serve on http://localhost:5173, API via VITE_API_URL
```

`VITE_API_URL` (e.g. `http://localhost:5090`) is copied into the served
`assets/app-config.json` by `scripts/sync-app-config.mjs` before serve/build.
Without it the dashboard uses `window.location.origin`.

## Verify

```sh
npm test            # vitest specs
npm run build       # Angular production build into dist/
npm run lint        # eslint
```

## Behavior notes

- `MessageId` dedupe is bounded (2,000 events / 500 alerts): Kafka and the
  alert outbox are at-least-once, so replays must not double-draw.
- Unticking equipment hides chart lines only; alerts stay global.
- Charts plot the sensor clock (`OccurredAt`), not arrival time.
- Sequence gaps are expected sensor-side loss, surfaced per device.
