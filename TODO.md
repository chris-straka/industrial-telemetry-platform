# Open work

Reviewed 2026-09-02. This file is a backlog, not a history of completed work. Docker Compose is the
supported demo path; the Kubernetes/Terraform material is still a prototype.

## 1. Extend the isolated failure harness

`scripts/e2e.sh` now creates a unique Compose project and volumes, submits a known accepted-ID set,
and verifies ingestion/gateway restart recovery, Postgres consumer retry, a durable poison-record
DLQ copy, alert-outbox recovery after a Kafka outage, and rejection of a TLS client without the
gateway certificate. It does not use the normal development project's containers or data.

Still add controlled fault injection for an ambiguous Kafka acknowledgement, then prove the
resulting replay remains one Postgres row and one rendered dashboard item per `MessageId`. The
current harness verifies server-side idempotency after ordinary retry, but it does not drive a real
browser or manufacture an ACK-after-write connection failure.

## 2. Complete authentication and transport security

Compose now protects edge-to-ingestion gRPC with a private development CA, a gateway client
certificate, and an explicit SHA-256 certificate allowlist. This demonstrates the shape of
per-gateway revocation, but it is not a production certificate lifecycle.

Remaining work:

- authenticate and encrypt sensor-to-edge traffic without turning one fleet-wide shared secret
  into every device's identity;
- add Kafka TLS/SASL and Postgres TLS plus workload-specific credentials;
- protect observability ingestion and operator UIs;
- replace the development CA bootstrap with managed enrollment, renewal, rotation, revocation, and
  audit; and
- decide whether the gateway allowlist should reload without restarting ingestion.

## 3. Prove or remove the production deployment prototype

The Helm and Terraform files are useful render/plan artifacts, not evidence of a production
deployment. Do not present `prod.Tiltfile`, `chart/`, or `infra/` as deployed until a real target
environment has exercised them.

Before enabling an apply workflow:

- publish immutable application images and configure the chart with registry digests;
- bootstrap and verify the encrypted remote Terraform backend and locking outside the same state
  it protects;
- create the cloud identity and SecretStore used by External Secrets, and provision runtime
  secrets without putting their values in Terraform variables or state;
- provide a production CA and a persistent, independently revocable identity and volume for each
  real gateway;
- run plan, apply, rollout, failure recovery, rollback, and destroy in a disposable account or
  cluster; and
- retain a reviewed promotion boundary before any persistent environment changes.

## 4. Calibrate and route observability alerts

Compose now provisions a checked-in Grafana dashboard, Prometheus alert rules, and named storage
for Prometheus, Loki, Tempo, and Grafana. The rules deliberately cover permanent sensor drops,
gateway backlog/rejections, diagnostics retries/DLQ failures, and the alert outbox.

The current thresholds are engineering defaults, not measured service-level objectives. Run
sustained load and outage drills to tune histogram buckets, p95/p99 thresholds, and `for` windows;
choose retention and disk budgets; then add an Alertmanager notification route and verify an
operator can follow a firing alert through metrics, logs, traces, quarantine, and the DLQ.

## 5. Optional cleanup and measurement

- Rename `Industrial.Data.ML` to something like `Industrial.ML.DetectorBuilder`; it creates an
  online IID detector artifact and does not train a learned model.
- Evaluate .NET Aspire only if it removes concrete Compose/observability work without obscuring the
  store-and-forward architecture.
- Code-split the dashboard if its current single production JavaScript chunk becomes a practical
  load-time issue.
- Record a reproducible load-test result before making throughput or sub-second latency claims.
