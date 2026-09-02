# Industrial Platform

.NET microservice platform that ingests telemetry from simulated industrial equipment and flags
anomalies with an online ML.NET detector plus an AI-generated diagnosis. Docker Compose is the
currently supported runtime; the cloud deployment files are explicitly unfinished.

The sensor emulator is deliberately best-effort, like a constrained device: it keeps acquiring
while the network is down and may shed samples when its bounded RAM/retry budget is exhausted.
Once the edge gateway returns `202`, however, the valid reading is on durable local storage and is
retained through cloud and gateway outages until the cloud settles it.

# Data flow

```
sensor-emulator (N devices, mints MessageId + OccurredAt)
                          │ HTTP/JSON, LAN
                          ▼
   ┌─────────────────── edge-gateway ────────────────────┐
   │  receiver ──► SQLite (WAL, fsync per commit)        │
   │                    │                                │
   │                    ▼                                │
   │              uploader ──► oldest-first, batched     │
   │                                                     │
   │  + bounded buffer w/ 429 + Retry-After backpressure │
   │  + exponential backoff w/ jitter                    │
   │  + OTel: queue depth, oldest message age            │
   └────────────────────────┬────────────────────────────┘
                            │ gRPC unary, 200 readings a call (HTTP/2), WAN
                            ▼
                        ingestion-api
                            │ produces telemetry-events, keyed by EquipmentId
                            ▼
   ┌───────────────────── Kafka ───────────────────────┐
   │                        ▲                          │
   ▼                        │                          ▼
diagnostics-worker          │                      web-api
 ├─ dedupes on MessageId    │                       ├─ consumes telemetry-events
 ├─ ML.NET anomaly check    │                       ├─ consumes telemetry-alerts
 ├─ saves to Postgres       │                       └─ relays live data via SignalR
 ├─ calls Gemini            │                                  │
 └─ alert outbox publisher ─┘                                  ▼
                                                         web-dashboard
```

# Delivery guarantees

There are two explicit reliability zones:

- **Before gateway `202`: best-effort.** A full sensor channel drops a new sample; an HTTP send
  that exhausts its bounded retry policy drops the dequeued sample. Separate metrics count both.
- **After gateway `202`: durable at-least-once.** The gateway has fsynced the reading to SQLite.
  Ambiguous gRPC/Kafka failures retry, and the reading is deleted only after ingestion names its
  `MessageId` accepted or permanently rejected.

Every reading carries an immutable `MessageId` minted by the sensor. SQLite protects live and
recently settled gateway IDs; Postgres has a unique index for Kafka replay. Duplicate live events
and alerts are also filtered by a bounded ID window in the dashboard.

> at-least-once delivery + an idempotent sink = effectively-once persisted state

Anomaly publication uses a transactional outbox: the diagnostic row and pending alert commit in
one Postgres transaction, then a separate worker publishes the alert. A crash after Kafka's ACK can
still publish twice, which is why the alert keeps the same `MessageId`.

Two clocks are carried end to end so an outage stays measurable:

| field | whose clock | meaning |
| --- | --- | --- |
| `OccurredAt` | sensor | when the reading was taken (event time) |
| `ReceivedAt` | cloud | when the cloud accepted it (processing time) |

Without `OccurredAt`, readings drained after a 30 minute outage would all claim to have
happened in the seconds it took to flush the queue.

A monotonic `SequenceNumber` makes gaps inside an emulator run visible. The emulator resets it on
restart and can intentionally drop before `202`, so a Postgres-only count is an audit signal—not
proof of the downstream guarantee or of an unseen tail.

# The demo

```sh
make demo-help          # prints the whole script
make upd                # everything up, queue depth ~0
make chaos-cloud-down   # kill the cloud; queue climbs, sensors keep producing
make chaos-gateway-kill # kill the gateway too, mid-outage; buffer survives
make chaos-cloud-up     # cloud returns; queue drains oldest-first
make verify             # require drained queue/outbox; report IDs, gaps, and lag
```

Watch `edge_queue_depth` and `edge_oldest_message_age_seconds` in Grafana while it runs. The audit
fails on an empty database, duplicate IDs, a non-empty edge queue, or pending alert outbox rows.

# Install

- [tilt](https://tilt.dev/)
- [Make](https://www.gnu.org/software/make/)
- [docker](https://www.docker.com/)
- [.NET](https://dotnet.microsoft.com/en-us/download)
- [Node](https://nodejs.org/en)
- [Terraform](https://developer.hashicorp.com/terraform/install)

# Ports

Compose publishes development ports on `127.0.0.1` only.

| service | host port | notes |
| --- | --- | --- |
| web-dashboard | 5173 | Vite dev server |
| web-api | 5090 | SignalR hub |
| ingestion-api | 5089 | REST (HTTP/1.1), manual testing only |
| ingestion-api | 5091 | gRPC (HTTP/2 cleartext) |
| edge-gateway | 5272 | sensor receiver + `/health` |
| grafana | 3000 | anonymous admin |
| kafka-ui | 8080 | |
| pgadmin | 5050 | |

# Notes

Design notes live in [docs/](docs/) — [Networking](docs/Networking.md),
[Kafka](docs/Kafka.md), [Observability](docs/Observability.md), [DB](docs/DB),
[ML](docs/ML). Known gaps and planned work are in [TODO.md](TODO.md).

Docker Compose is the supported runnable/demo path. The Helm, production Tilt, and Terraform files
are an unfinished prototype; see the ranked production-deployment work in [TODO.md](TODO.md) before
treating them as deployable infrastructure.
