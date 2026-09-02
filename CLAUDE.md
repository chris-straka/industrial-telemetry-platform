# IndustrialPlatform

.NET telemetry platform: fake industrial sensors -> edge gateway -> cloud ingestion ->
Kafka -> ML anomaly detection -> React dashboard.

This is a **portfolio project**. It exists to be walked through on video and defended in
interviews, so the bar is "every decision is explainable", not "it works". Three
consequences that override normal instincts:

1. **Comments are load-bearing.** Long explanatory comments are deliberate teaching
   notes, not clutter. Never strip or condense them. If a comment is factually wrong,
   correct the claim in place and keep the surrounding reasoning.
2. **Rejected alternatives go in `docs/`, not in the code.** "Simple" is not automatically
   right and neither is "clever"; what matters is being able to say what else was on the
   table and why this won — but that argument belongs in a design note, where it has room
   to be made properly. Do not add a comment naming an alternative to a declaration or a
   method body. A code comment says what the code does and why it is safe; the `docs/`
   note says what else was considered.

   The reason given has to survive scrutiny on its own terms — do not credit an
   alternative with a property it would not have had.
3. **Comments explain the code that is there, never the code that was.** "This replaced
   X" is archaeology and git already has it. If a superseded approach taught something
   worth keeping, that lesson goes in `docs/`, and the comment states the surviving
   reason on its own terms.

Long-form design notes live in `docs/`. `TODO.md` is the known-gaps list.

## The invariant everything protects

No telemetry reading is ever lost or double-counted, even when the cloud, the network,
or the gateway itself dies.

The mechanism, in order — do not break any link without saying so explicitly:

- The **sensor** mints `MessageId` (idempotency key) and stamps `OccurredAt` (event
  time). Never regenerate either downstream.

  An idempotency key protects every hop *downstream* of where it is created, and no hop
  upstream. Minting it at the gateway would leave the sensor->gateway POST unprotected:
  the sensor retries a POST whose response was lost, the gateway generates a fresh id for
  the retry, the unique index sees two different ids and stores both — and that duplicate
  is now indistinguishable from a real second reading all the way to Postgres. So mint it
  at the origin of the data, which is as far upstream as it can go.
- The **gateway** writes to SQLite in WAL mode with `synchronous=FULL` *before* it ACKs
  the sensor, then uploads oldest-first.

  It deletes a record only once the cloud has settled it. The gateway sends a batch of 200
  over one gRPC stream; the server answers with two lists of `MessageId`s —
  `accepted_message_ids` for what it durably produced to Kafka, and
  `rejected_message_ids` for what it will refuse no matter how often it is sent. The
  gateway deletes both and re-sends everything named in neither, so a reading is only
  forgotten once the cloud has said which of the two it is. If the response never arrives
  at all, it deletes nothing and re-sends everything.

  Ids rather than a count of the batch prefix, because a count can only describe a server
  that stops dead at its first failure, and this one skips an invalid reading and keeps
  going. That is also what lets the gateway drop a refused record without first resending
  the batch one reading at a time to find it.
- **Two unique indexes on `MessageId`, guarding two different hops.** The gateway's
  SQLite index absorbs a retried sensor POST. Postgres's index absorbs a re-sent gateway
  batch and a Kafka redelivery. They are not the same defense.
- **Every retryable hop is at-least-once**, not just one of them: sensor->gateway (Polly
  retries the POST), gateway->cloud (ambiguous stream failures are re-sent), Kafka->worker
  (the offset commits after processing, so a crash replays). Correctness comes from
  idempotent writes at each hop, not from trying to make delivery exact. Neither gRPC nor
  Kafka gives you exactly-once.
- `OccurredAt` (sensor clock) and `ReceivedAt` (cloud clock) are both carried end to end.
  Without event time, readings drained after an outage would all claim to have happened
  during the flush.
- `SequenceNumber` is monotonic per device, so gaps prove loss:
  `MAX(SequenceNumber)` must equal `COUNT(*)` per `EquipmentId`.

`make verify` checks all of this. If a change makes duplicates or gaps non-zero, the
change is wrong.

**WAL** = Write-Ahead Log. SQLite's default rollback journal copies the *original* pages
out to a journal file and then mutates the main database in place, so a writer locks out
readers. WAL inverts it: new pages are appended to a separate `-wal` file and the main
database is left alone until a checkpoint folds them in, so the uploader can read batches
while the receiver is still writing. A commit is durable once its WAL record is fsync'd,
which is what `synchronous=FULL` forces on every commit — `NORMAL` would survive a process
kill but could lose the last few commits on power loss, and an edge device is exactly the
machine that loses power.

## Layout

| project | role |
| --- | --- |
| `Industrial.Shared` | `OTelOptions` — config shared by every service. Future home of the Kafka envelope contract. |
| `Industrial.Sensor.Emulator` | fake devices. Two loops: `AcquisitionWorker` (per-device tasks) and `TransmissionWorker`, joined by a bounded `Channel<T>`. HTTP/JSON to the gateway. |
| `Industrial.Sensor.EdgeGateway` | receiver + SQLite buffer + gRPC uploader |
| `Industrial.Ingestion.Api` | gRPC server (8081) + legacy REST (8080), produces to Kafka |
| `Industrial.Diagnostics.Worker` | Kafka consumer, dedupe, ML.NET, Gemini, Postgres |
| `Industrial.Web.Api` | Kafka consumer, SignalR relay |
| `Industrial.Web.Dashboard` | React + Vite |
| `Industrial.Data.ML` | trains `model.zip` |
| `src/Protos` | shared `telemetry.proto` |

The SDK of each project describes what that process actually is (Worker / Web / plain
console). See `docs/NET.md`.

## Configuration

Every service binds typed options at startup and validates them:

```csharp
builder
    .Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

Rules:

- **No fallback defaults.** Every value is set explicitly in `docker-compose.yaml` and
  `appsettings.Development.json`, so a missing value means misconfiguration, not consent
  to a default. `Emulator__DeviceCont=1000` (typo) must fail at boot, not silently run 4
  devices.
- **Nothing outside `Program.cs` touches `IConfiguration`.** Workers take `IOptions<T>`,
  so a class sees only its own typed fields instead of the whole config by string key.
- `[Required]` rejects null, empty AND whitespace. The C# `required` keyword does not —
  the binder uses reflection, which skips it. Use the attribute.
- `[Range(1, N)]` doubles as a presence check on ints (unset binds to 0, which fails).
  It cannot when 0 is legal — see `EmulatorOptions.ReplicaId`, which is 0-based and so is
  `int?` + `[Required]` instead.
- A few values are bound eagerly with `.Get<T>()` because service registration needs them
  before the container exists. Everything else is injected.

For a single loose string, a direct `IsNullOrWhiteSpace` check is fine and Options is
overkill. See `docs/NET.md`.

## Commands

```sh
make upd          # docker compose up -d
make demo-help    # the store-and-forward outage demo, step by step
make verify       # duplicates = 0, missing = 0, end-to-end lag
make queue        # current edge buffer depth
make migrate name=X && make db-update   # EF migrations (Diagnostics.Worker owns them)
./scripts/AI.sh [path...]               # dump repo as markdown for a bot
```

## Conventions

- Config keys are read with a **colon** (`Cloud:ApiUrl`). Docker sets `Cloud__ApiUrl`;
  .NET rewrites `__` to `:` at load time, so reading the double-underscore form returns
  null. This has bitten this repo twice.
- Feature-folder layout: `Features/<Area>/`, infra in `Infrastructure/`.
- Only `Diagnostics.Worker` owns EF migrations. The gateway uses `EnsureCreated` on
  purpose — its database is a transient buffer, not a system of record.
- OTel instrument names are dotted in code (`edge.queue.depth`); Prometheus renders them
  underscored.
- Emulator replicas are partitioned by `Emulator__ReplicaId`, **0-based** to match a k8s
  StatefulSet ordinal (0, 1, 2 for the three fleet services). Replica N owns
  `EQ-{N*DeviceCount}`..`EQ-{(N+1)*DeviceCount-1}`. Do NOT use `docker compose --scale` on
  it — every replica would get identical env, collide on device ids, and make
  `make verify` report phantom duplicates. Use `make fleet`. See `docs/docker.md`.
- The sensor's `Channel<T>` is a DECOUPLING buffer (RAM, dies with the process), not a
  durability buffer. The gateway is the only thing that survives outages. Keep it that way.

## Docs index

| file | covers |
| --- | --- |
| `docs/Networking.md` | HttpClient pooling, thundering herd, HOL blocking, HTTP/1.1→3, why batches, buffer durability |
| `docs/NET.md` | project SDKs, `FrameworkReference`, `IHttpClientFactory` internals, DI registration vs Spring, config layers and gotchas |
| `docs/Kafka.md` | brokers, partitions, offsets, delivery semantics, poison messages |
| `docs/Kubernetes.md` | Deployment vs StatefulSet, PVCs, ordered rollout |
| `docs/docker.md` | replica identity, why `ReplicaId` is 0-based, Swarm `.Task.Slot`, why N devices per container |
| `docs/Fintech.md` | industrial→payments mapping, card-network store-and-forward, buffer durability principle |
| `docs/Bug-AcquisitionCoupling.md` | the acquisition/transmission coupling bug, found at two layers |
| `docs/UploadFailures.md` | upload outcome classification, which gRPC statuses are permanent, dropping a refused record vs stalling the queue |
| `docs/Observability.md` | OTel vocabulary, Loki/Prometheus/Tempo, span propagation, gauge sampling |
| `docs/Concurrency.md` | `Interlocked` vs locks, `lock xadd`, MESI, false sharing, why `_dropped` is shared |
| `docs/DB/Postgres.md` | heap vs clustered index, pages, B+trees, why `MessageId` is a v7 uuid |
| `docs/DB/SQLite.md` | file header, rollback journal vs WAL, why the writer locks readers out |
| `docs/DB/EF.md` | EDM, the three files a migration generates, what the snapshot decides |
| `docs/DB/Normalization.md` | normal forms |
| `docs/DB/NoSQL.md` | when to NoSQL, schema-on-read, eventual consistency, why not MongoDB |
| `docs/ML/MLOps.md` | why MLOps is in scope here, tooling |
| `docs/C#.md` | language mechanics — extension methods, records, etc. |
| `docs/OOP.md` | four pillars, composition vs inheritance, Law of Demeter, Tell Don't Ask |
| `docs/Infra.md` | Terraform and Compose |

## Current state

The store-and-forward transition is IN PROGRESS. `TODO.md` has the ranked backlog; the
top item is that there are no automated tests at all.
