# Industrial Platform

Cloud-hosted .NET microservice platform that ingests telemetry from simulated industrial
equipment and flags anomalies with ML.NET plus an AI-generated diagnosis.

Readings are written to durable local storage on an edge gateway before they are
acknowledged, so sensors keep producing through a total cloud outage and the queue drains
when it ends. `make verify` measures the result: zero lost, zero duplicated.

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
 └─ produces alerts ────────┘                                  ▼
                                                         web-dashboard
```

# Delivery guarantees

The transport is at-least-once. Exactly-once is not something gRPC or Kafka hands you:
if a connection dies after the server commits but before the ACK arrives, the sender
cannot know what happened, so it must re-send.

What makes that safe is that every reading carries an immutable `MessageId` minted by
the **sensor** — not by the gateway, not by the API. A unique index on it in both SQLite
and Postgres turns a duplicate delivery into a no-op.

> at-least-once delivery + idempotent consumer = effectively-once processing

Two clocks are carried end to end so an outage stays measurable:

| field | whose clock | meaning |
| --- | --- | --- |
| `OccurredAt` | sensor | when the reading was taken (event time) |
| `ReceivedAt` | cloud | when the cloud accepted it (processing time) |

Without `OccurredAt`, readings drained after a 30 minute outage would all claim to have
happened in the seconds it took to flush the queue.

A monotonic `SequenceNumber` per device makes loss detectable too: if a device produced
N readings, `MAX(SequenceNumber)` must equal `COUNT(*)`. Any shortfall is data lost.

# The demo

```sh
make demo-help          # prints the whole script
make upd                # everything up, queue depth ~0
make chaos-cloud-down   # kill the cloud; queue climbs, sensors keep producing
make chaos-gateway-kill # kill the gateway too, mid-outage; buffer survives
make chaos-cloud-up     # cloud returns; queue drains oldest-first
make verify             # duplicates = 0, missing = 0, and the lag spike
```

Watch `edge_queue_depth` and `edge_oldest_message_age_seconds` in Grafana while it runs.

# Install

- [tilt](https://tilt.dev/)
- [Make](https://www.gnu.org/software/make/)
- [docker](https://www.docker.com/)
- [.NET](https://dotnet.microsoft.com/en-us/download)
- [Node](https://nodejs.org/en)
- [Terraform](https://developer.hashicorp.com/terraform/install)

# Ports

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
