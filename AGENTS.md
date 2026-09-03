# IndustrialPlatform

.NET telemetry portfolio project:

`sensor emulator -> edge gateway -> ingestion API -> Kafka -> diagnostics/Postgres -> SignalR dashboard`

The project is meant to be demonstrated and defended in an interview. Prefer a smaller claim the
code can prove over a larger claim hidden behind comments.

## Reliability boundary

The sensor emulator is intentionally best-effort. Acquisition never waits for the network:

- A full in-memory channel drops the new sample and increments `sensor.telemetry.dropped`.
- The transmission loop uses a bounded HTTP resilience policy. If it never receives a `202`, it
  discards that dequeued reading and increments `sensor.telemetry.delivery_dropped`.
- These losses model constrained real devices. Do not describe sensor-to-gateway delivery as
  at-least-once.

The durable contract begins when the edge gateway returns `202 Accepted` for a valid reading:

1. The sensor mints `MessageId` and `OccurredAt`; no downstream component regenerates either.
2. The gateway validates bounded content, writes the reading to SQLite with WAL and
   `synchronous=FULL`, and only then returns `202`.
3. The gateway keeps the row until ingestion names its `MessageId` as accepted or permanently
   rejected. IDs named in neither list remain queued for retry.
4. Ingestion names only deterministic validation failures as rejected. Kafka/producer failures
   leave the ID unnamed so the gateway retains it.
5. Kafka delivery and the diagnostics consumer are at-least-once. Diagnostics commits an offset
   only after processing succeeds and rewinds the record on failure.
6. Diagnostics publishes a malformed record or tombstone to `telemetry-events-dlq` before
   committing its source offset. If that publish fails or is ambiguous, it rewinds the source
   record instead of silently advancing.
7. Postgres has a unique `MessageId` index, so a Kafka replay is a no-op.
8. An anomaly row and its alert outbox row commit in one Postgres transaction. The publisher may
   emit a duplicate after an ambiguous Kafka ACK; dashboard alert dedupe uses `MessageId`.

The gateway retains recently settled IDs after deleting live queue rows. This absorbs a delayed
retry whose original `202` was lost. Retention is bounded; it is not a permanent global ledger.
While a rejected row remains in the longer-lived quarantine, that ID is also treated as already
known so expiry of its settled marker cannot reintroduce the same poison reading.

A cloud rejection is explicit permanent loss, not success: it is logged, metered, and copied into
a bounded SQLite quarantine before leaving the live queue so a poison row cannot block every newer
reading. Quarantine is forensic retention, not another delivery queue; operators can inspect it at
`GET /buffer/quarantine?limit=100`. Gateway admission and ingestion validation should remain
aligned so this path is exceptional.

`make verify` is an audit, not a mathematical proof. It requires a drained edge queue, a drained
alert outbox, non-empty Postgres data, and no duplicate IDs. It reports sequence gaps by inferred
emulator run. It cannot infer a sensor-side tail drop without an origin-side run total.

## Rules that protect the contract

- Preserve `MessageId` unchanged across every hop.
- Never turn a transient/internal exception into `rejected_message_ids`; that tells the gateway to
  delete its durable copy.
- Keep gateway receive and settle mutations under `BufferMutationGate`. `BufferDepth.TryReserve`
  is the atomic `MaxDepth` admission decision; do not overwrite it with a racing periodic count.
- The edge endpoint must reject inputs that could later exceed the gRPC/Kafka contract. Keep its
  bounds aligned with `TelemetryReadingValidator` and `UploaderOptions.BatchSize`.
- Kafka event JSON uses `Industrial.Shared.TelemetryEnvelope`; do not create a private duplicate
  DTO in a consumer.
- A poison Kafka record may be committed only after its DLQ copy is acknowledged. DLQ failure or
  an ambiguous acknowledgement must leave the source offset retryable.
- The current protobuf response baseline is `accepted_message_ids = 2` and
  `rejected_message_ids = 3`. Once a field is deployed, never renumber or reuse it; reserve removed
  deployed tags. Regression tests pin the current tags.
- `ModelEngine` is an online IID spike detector, not a model trained from `training_data.csv`.
  State is isolated per `EquipmentId`, warmed from recent Postgres readings, and invalidated after
  a failed DB transaction.
- A Web API replica owns only its local SignalR clients, so every replica uses a distinct Kafka
  group and receives the full live stream. Do not replace that with a shared group without adding
  a SignalR backplane.

## Comments and design notes

Explanatory comments are teaching material for the portfolio walkthrough. Preserve useful
reasoning when editing nearby code. If a comment is wrong, correct the fact rather than leaving it
as archaeology.

Code comments explain the current implementation and its safety. Rejected alternatives and longer
trade-off discussions belong in `docs/`. `TODO.md` contains only open work; remove or rewrite an
entry when implementation changes its premise.

## Layout

| project | role |
| --- | --- |
| `Industrial.Shared` | OTel options and shared Kafka envelope contracts |
| `Industrial.Sensor.Emulator` | best-effort fake devices with acquisition/transmission loops |
| `Industrial.Sensor.EdgeGateway` | bounded SQLite store-and-forward receiver and gRPC uploader |
| `Industrial.Ingestion.Api` | validates gRPC batches and produces `telemetry-events` |
| `Industrial.Diagnostics.Worker` | Kafka consumer, online IID detection, Postgres, Gemini, alert outbox |
| `Industrial.Web.Api` | per-instance Kafka broadcast subscription and SignalR relay |
| `Industrial.Web.Dashboard` | React/Vite live dashboard with bounded `MessageId` dedupe |
| `Industrial.Data.ML` | builds the IID detector configuration/schema artifact |
| `tests/*` | focused regression suites for the reliability seams |

Use feature folders under `Features/<Area>/`; cross-cutting process concerns belong under
`Infrastructure/`.

## Configuration

Runtime services bind typed options and validate them at startup.

- Docker environment key `Foo__Bar` becomes configuration key `Foo:Bar`.
- Workers receive `IOptions<T>` rather than unrestricted `IConfiguration`.
- Use `[Required(AllowEmptyStrings = false)]` for required strings and `[Range]` for required
  numeric values where zero is invalid.
- Do not silently substitute a fallback for a missing deployment value.
- `AppDbContextDesignFactory` is the design-time exception: EF CLI tools use it so they do not
  start the worker's infinite hosted-service loops.

Only Diagnostics owns production EF migrations. The gateway uses `EnsureCreated` because SQLite
is a transient queue; explicit `CREATE TABLE IF NOT EXISTS` statements support compatible additions
without throwing away an existing outage buffer.

## Required checks

Run checks proportional to the change. Before handing off a repository-wide change, run all of:

```sh
dotnet build IndustrialPlatform.slnx --no-restore
dotnet test IndustrialPlatform.slnx --no-restore
cd src/Industrial.Web.Dashboard && npm run lint && npm run build
docker compose config --quiet
dotnet ef migrations has-pending-model-changes --project src/Industrial.Diagnostics.Worker --no-build
```

Useful local commands:

```sh
make upd
make fleet
make demo-help
make queue
make verify
./scripts/e2e.sh
make migrate name=DescriptiveName
make db-update
```

## Deployment status

Docker Compose is the supported runnable path. Its edge-to-ingestion gRPC hop uses mutual TLS with
a private development CA and an allowlisted gateway certificate fingerprint. That is a local
demonstration of per-gateway identity, not a production PKI. Sensor-to-edge, Kafka, Postgres, and
observability traffic remain plaintext and unauthenticated inside the Compose network.

The Helm/Tilt/Terraform material is still a render/plan prototype, not a production deployment
claim. Linting templates or producing a Terraform plan does not prove a real cluster, identity and
secret integration, image publication, remote state, rollout, rollback, or failure recovery.
