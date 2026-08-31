# Status

Reviewed after the edge-gateway transition. Items marked DONE are implemented; the rest
are still open, roughly in the order I'd tackle them.

---

# TODO: No automated tests at all

Current: verified by hand with the emulator and `make verify`.
Risk: the biggest single gap in this repo right now. Every durability claim is asserted,
not proven, and a reviewer looking for engineering rigour checks for tests first.

Fix: Testcontainers. Spin up real Kafka and Postgres in CI and assert the invariant
directly — produce N readings, kill the ingestion API mid-run, restart it, assert
`ingested == produced` and `duplicates == 0`. That test IS the demo, automated.
Cheap unit tests worth having alongside it: the uploader's partial-ACK delete
(accepted=150 of 200 deletes exactly the first 150) and the receiver's duplicate path.

# TODO: The emulator's new metrics have never been observed

Current: `SensorMetrics` exists and `.WithMetrics(...)` is wired into the emulator's OTel
block. Counters fire from `AcquisitionWorker` (`acquired`, `dropped`) and
`TransmissionWorker` (`sent`, `rejected`, `failed`), plus an observable gauge reading
`channel.Reader.Count`. All of it is verified by COMPILATION ONLY -- nothing has run
against a live collector.

Risk: an instrument that is never scraped is indistinguishable from one that does not
exist. `sensor.telemetry.dropped` in particular is the only place sensor-side loss is ever
visible -- a reading dropped at the channel gets no row anywhere, so `make verify` counts
it as a sequence gap with nothing to attribute it to.

Fix: `make upd`, let it run, and confirm all five counters and the gauge appear in
Prometheus (dotted in code, underscored in PromQL: `sensor_telemetry_dropped`). Then check
the gauge actually moves -- kill the gateway and `sensor.channel.depth` should climb from
~0 toward `BufferCapacity` (10_000) before any drop is recorded. If depth never rises, the
gauge callback is wrong; if it pins at 10_000 with no drops, the counter is not wired.

# TODO: No latency histogram anywhere

Current: every send-path instrument is a Counter, so the metrics say how many sends
happened and never how slow the worst ones were. The emulator's POST to the gateway and
the gateway's gRPC upload are both invisible on that axis.

Fix: a `Histogram<double>` on each hop, recorded around the call. That also makes the
outage demo sharper -- the queue-depth gauge shows the backlog, but nothing currently
shows the latency spike that precedes it.

# TODO: No CI that builds C#

Current: `.github/workflows/` only has terraform-plan and terraform-apply.
Risk: nothing catches a broken build except me running it.

Fix: a `dotnet build` + `dotnet test` workflow on push. Add `buf lint` and
`buf breaking --against` for the proto while there — a renumbered field is a wire-compat
break that no compiler catches.

# NOT DOING (yet): the outbox pattern

A DUAL WRITE is one request that writes to two stores which cannot be committed together —
say a row in Postgres and a message in Kafka. Either can succeed while the other fails, and
then the two disagree with no transaction to roll back.

The OUTBOX PATTERN turns that into a single write. The request writes its row AND an
"outbox" row in one database transaction; a separate process reads the outbox afterwards
and publishes to Kafka. One commit, so the two stores can never disagree about whether the
thing happened.

`TelemetryService` has no dual write. It writes to exactly one store — Kafka — so there is
nothing for a shared transaction to make atomic.

What I was actually worried about when I wrote this entry is that a Kafka outage leaves the
ingestion API unable to accept anything. That is real, but it is an AVAILABILITY problem,
not a consistency one, and the buffer that absorbs it already exists one hop upstream:
Kafka down means `ProduceAsync` throws, the response names fewer ids, and the gateway keeps
those rows on disk until it can send them again. An outbox here would put a durable log in
front of a durable log, insuring against the outage of the component whose entire job is
being a durable log.

The trigger to revisit: the day this service writes to Postgres AND produces to Kafka in
the same request. That is a genuine dual write, and the outbox is then the answer.

# TODO: Dead letter for readings the cloud refuses

Current: `UploaderWorker` deletes every id the cloud returns in `rejected_message_ids`,
logs them at Error, and counts `edge.telemetry.rejected`. Dropping is what stops one
refused row blocking the queue forever, since it always sits in the oldest batch.
Risk: dropped means gone, and only the cloud's logs say why it was refused. Same
auditability gap as the Kafka side below.

Fix: a local `quarantine` table in the same SQLite file, written in the same transaction
as the delete, holding the row so a human can look at what was thrown away.

# TODO: Dead Letter Queue

Current: a poison pill is logged and skipped (`TelemetryConsumerWorker`).
Risk: skipped means gone. No audit trail of what was dropped or why.

Fix: route failures to a `telemetry-errors` topic with the original payload plus the
exception. Critical for auditability in fintech and industrial systems alike.

# TODO: Trace context across Kafka

Current: OTel auto-instrumentation covers HTTP and gRPC, and W3C `traceparent`
propagates over the gRPC hop, so gateway -> cloud stitches into one trace.
Risk: Kafka does NOT propagate trace context automatically. The trace dies at the
producer and a new one starts at the consumer, so there is no single Gantt chart of a
reading's whole journey.

Fix: inject `traceparent` into Kafka message headers on produce, extract and restore it
on consume. That closes the last gap and makes an outage visible as one ten-minute-wide
trace.

# DONE: Trace context across the SQLite buffer

Was: nothing on `TelemetryRecord` remembered the sensor's trace, so the trace died at
the 202 and the uploader started a fresh one. The gateway -> cloud hop stitched; the
sensor -> gateway -> cloud journey did not.

`TelemetryRecord.TraceParent` is captured at receive and attached to the `edge.upload`
span as a span LINK rather than restored as the parent. Parent-child means "this ran because
that called it and is waiting". After buffering, neither holds: the sensor's request
finished ten minutes ago, and the upload fires because the uploader loop found rows, not
because any one reading asked for it. A batch is a fan-in of up to 200 unrelated traces
into one gRPC call, and a link is the relation that says "related to" without claiming
"caused by".

Rejected: a `traceparent` field per reading in the proto, keeping each reading in its own
trace end to end. It draws the better demo -- one continuous ten-minute bar per reading
-- but costs a proto change, 200 spans per post-outage batch, and it asserts a causal
chain that is not there.

# TODO: Schema Evolution (partially done)

DONE for the gateway -> cloud hop: `telemetry.proto` is a shared schema both sides
generate from, so a renamed field is a compile error.

Current: the Kafka message value is still a JSON string.
Risk: `System.Text.Json` silently leaves unmatched properties at their defaults. This
already bit me — the API wrote `MessageId`, the worker's DTO didn't have the field, and it
was null forever. No exception, no log.

Fix, in order of cost:
1. `Industrial.Contracts` project holding the envelope record, referenced by the API, the
   worker and the web API. A rename becomes a compile error. Cheap, most of the benefit.
2. Protobuf/Avro + Schema Registry. Worth it when there are producers I don't own.

# TODO: Auth

Current: nothing. Every hop is unauthenticated plaintext.
Fix: M2M authentication with Keycloak — the gateway needs a client secret to talk to the
ingestion API. Proves service-to-service security, not just user login.

For the edge hop specifically the realistic answer is mTLS with a per-device client
certificate, because that is how you revoke a single compromised gateway in the field
without rotating a shared secret across the fleet.

# TODO: Diagnostics.Worker is a .Web project that serves nothing

Current: it uses `WebApplication.CreateBuilder` and maps no endpoints, so it pays for the
ASP.NET Core shared framework and gets nothing back.
Risk: mild, but it also means k8s has no liveness/readiness probe to hit, and there is a
Helm chart in `chart/` that would want one.

Fix: keep `.Web` and add `/health` (plus `/health/ready` gated on the Kafka consumer
actually being assigned partitions). That makes the SDK choice honest AND gives Istio
something to route on. The alternative — switching it to `.Worker` — is worse here,
precisely because the probe is genuinely wanted.

# TODO: The Helm chart predates the edge gateway

Current: `chart/templates/custom-apps.yaml` is a generation behind the store-and-forward
refactor and would not `helm template` cleanly if it were run.

- There is no `edge-gateway` Deployment and no `edge-gateway` image in `values.yaml`, so
  the one piece the whole durability story rests on does not exist in the k8s path at all.
- The first `range` block is supposed to be the services-with-ports; it actually lists
  `diagnostics-worker` and `sensor-emulator` (both workers), while `web-api`,
  `ingestion-api` and `web-dashboard` are missing entirely. Those two therefore render
  TWICE — once with an empty `containerPort:`, because the dict has no `port` key.
- `sensor-emulator` is handed `Ingestion__ApiUrl`, which nothing binds. It needs
  `Gateway__Url` plus the `Emulator__*` block, and `ValidateOnStart` means a missing
  value is a crash at boot, not a default.

Risk: compose is the dev path and works; k8s is the intended prod path and is untested
fiction. Worse in an interview than having no chart, because it looks finished.

Fix, in order: add `edge-gateway` as a StatefulSet, not a Deployment — its SQLite buffer
is the one piece of state that must survive rescheduling, and a Deployment would point
every replica at the same PVC (`docs/Kubernetes.md` covers why that is corruption rather
than replication). Then split the template's service and worker ranges properly, and give
`sensor-emulator` the real config contract. If the emulator ever runs more than one pod
there, `Emulator__ReplicaId` comes straight off the StatefulSet ordinal
(`ORDINAL=${HOSTNAME##*-}`), which is what 0-based buys.

# TODO: Rename Industrial.Data.ML

It generates synthetic training data and trains `model.zip`. The name reads like a data
access library. `Industrial.ML.Training` matches the `Industrial.<Area>.<Thing>`
convention used everywhere else.

# TODO: Aspire

https://aspire.dev/ — would replace a chunk of the compose wiring. Evaluate whether it
adds anything over what's already working, or just churn.

---

# DONE: Idempotency and retries

`MessageId` is minted by the sensor and carried unchanged through every hop. Unique index
in SQLite (absorbs a retried sensor POST) and in Postgres (absorbs a re-sent gateway batch
or a Kafka redelivery). The worker dedupes before ML inference, because the time-series
engine is stateful and a replayed duplicate would corrupt its window.

# DONE: MessageId is a uuid, and ids are time-ordered

`TelemetryReading.MessageId` is a `Guid` (Postgres `uuid`, 16 bytes) instead of a `string`
(`text`, 37), on the index every message touches. `Id` and the sensor's minted `MessageId`
are both `Guid.CreateVersion7()` -- time-ordered, so inserts land on the index's rightmost
page instead of splitting pages at random. The wire stays a string (protobuf has no uuid
scalar), so the consumer parses at the boundary with `Guid.TryParse`, which folds a
malformed id into the same poison-pill path as a missing one.

The gateway's SQLite `MessageId` is deliberately still a string: EF's SQLite provider
stores `Guid` as TEXT anyway, so there is no size win, and that database is a transient
buffer rather than the system of record.

Migrations were squashed to a single `InitialCreate` at the same time. See
`docs/DB/Postgres.md` for why that beat appending a migration -- EF's `AddColumn` leaves
its backfill `defaultValue` on the column permanently, which would have made a forgotten
`MessageId` silently become `Guid.Empty`.

# DONE: Poison pill messages

The consumer loop try/catches per message and continues. Messages with no `MessageId` are
discarded explicitly, since a message that can't be deduplicated can't be accepted without
breaking the guarantee. (Still needs the DLQ above so they're not silently lost.)

# DONE: Hardcoded connection string

Moved to `appsettings.json` via `GetConnectionString("IndustrialDb")`.

# DONE: Kafka consumer closing

`consumer.Close()` in a `finally`. Commits final offsets and leaves the group cleanly,
instead of making the broker wait out `session.timeout.ms` before rebalancing on every
deploy.

# DONE: Producer lifecycle

`ApplicationStopping` flushes and disposes the producer in both the API and the worker.

# DONE: Kafka advertised listeners

Separate INTERNAL (`kafka:9092`) and EXTERNAL (`localhost:9094`) listeners, so containers
and host tools each get an address that resolves for them.

# DONE: Protocol Buffers

Used on the gateway -> cloud hop. See `src/Protos/telemetry.proto`.

# DONE: ML.NET integration

`ModelEngine` wraps a `TimeSeriesPredictionEngine` loaded from `model.zip`, replacing the
hardcoded threshold.

# DONE: Real-time dashboard

React + Vite, fed by the web API over SignalR.
