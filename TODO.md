# Open work

Reviewed 2026-09-02. This file is a backlog, not a history of completed work. Docker Compose is the
supported demo path; the Kubernetes/Terraform material is still a prototype.

## 1. Add a real end-to-end failure test

The focused xUnit suites and CI now cover admission bounds, concurrent capacity, schema tags,
validation, detector state isolation, and the outbox model. They do not yet prove the whole running
pipeline.

Use Testcontainers (or an equivalent isolated Compose harness) to start Kafka, Postgres, ingestion,
gateway, and diagnostics; submit a known set of gateway-accepted IDs; interrupt Kafka/ingestion and
restart the gateway; then assert:

- every accepted ID eventually exists once in Postgres;
- the edge queue drains to zero;
- failed diagnostic processing does not advance past a Kafka offset;
- an alert publish failure leaves a pending outbox row that later publishes;
- ambiguous ACKs may duplicate Kafka delivery but not Postgres or dashboard state.

The test needs an origin-side accepted-ID ledger. Sequence numbers alone cannot detect a dropped
tail, and the emulator intentionally drops before `202`.

## 2. Add readiness, not just liveness

All web-hosted .NET processes now expose `/health`, but those endpoints only prove the process can
answer HTTP. Add separate `/health/ready` checks for the dependencies required to serve new work:

- ingestion: Kafka topic metadata/producer readiness;
- diagnostics: Postgres migration state plus Kafka assignment;
- Web API: Kafka assignment;
- gateway: SQLite writable, while cloud reachability remains a metric rather than liveness.

Wire the distinction into Compose/Kubernetes only after the checks exist.

## 3. Quarantine permanent cloud rejections

Strict edge and ingestion validation should make `rejected_message_ids` exceptional, but the
gateway still deletes a rejected live row so one poison record cannot block the oldest-first queue.
Add a bounded local quarantine table containing the original reading, rejection time, and reason
code. Write quarantine + settled marker + live-row delete in one SQLite transaction, expose an
operator inspection command, and define retention so quarantine cannot fill the disk.

## 4. Make anomaly decisions auditable

Persist detector score, p-value, detector configuration/version hash, and the amount of warm-up
history beside `IsAnomaly`. Right now logs contain score/p-value but historical rows cannot explain
which configuration produced the decision.

Also define retention for successfully published outbox rows. Pending rows must never expire;
published rows do not need to grow forever.

## 5. Add poison-message quarantine or DLQ

Diagnostics intentionally commits malformed Kafka records after incrementing
`worker.telemetry.discarded`, because retry cannot repair invalid JSON. A DLQ/quarantine topic would
make the payload inspectable. Protect it from recursive failure and do not let a DLQ outage advance
the source offset silently.

## 6. Add authentication and transport security

Every internal hop is plaintext and unauthenticated today. For a realistic edge fleet, prefer mTLS
with a per-gateway client certificate so one device can be revoked independently. If demonstrating
identity-provider flows, use short-lived M2M tokens for cloud services; do not treat one shared
secret across every simulated device as the final design.

## 7. Either finish or remove the production deployment prototype

Do not present `prod.Tiltfile`, `chart/`, or `infra/` as deployable yet. Before enabling apply jobs:

- make `helm dependency build` plus `helm template` pass in CI;
- model the edge gateway as stateful identity plus per-replica persistent storage;
- render every application once with the exact validated configuration keys;
- choose one secret-management path and create its identity/SecretStore;
- repair Terraform provider wiring and supply a remote encrypted backend with locking;
- make plan inputs complete without placing secrets in tfvars or Terraform state;
- add a reviewed promotion boundary before production apply.

The current Terraform apply workflow should not be used until those decisions are made.

## 8. Exercise observability under failure

The sensor and edge now expose logical delivery-duration histograms in addition to counters and
queue gauges. Run the outage demo against a live collector and add checked-in dashboards/alerts for:

- sensor acquisition drops and post-retry delivery drops;
- edge depth, oldest age, shed count, rejection count, and cloud reachability;
- diagnostics processing retries and pending alert outbox rows;
- p95/p99 sensor HTTP and edge gRPC logical-attempt duration.

Decide whether local Prometheus/Loki/Tempo history should use named volumes; it is currently
disposable with the containers.

## 9. Optional cleanup

- Rename `Industrial.Data.ML` to something like `Industrial.ML.DetectorBuilder`; it creates an
  online IID detector artifact and does not train a learned model.
- Evaluate .NET Aspire only if it removes concrete Compose/observability work without obscuring the
  store-and-forward architecture.
- Code-split the dashboard if its current single production JavaScript chunk becomes a practical
  load-time issue.
