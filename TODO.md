# Open work

Reviewed 2026-09-02. This file is a backlog, not a history of completed work. Docker Compose is the
supported demo path; the Kubernetes/Terraform material is still a prototype.

## 1. Extend the isolated failure harness

`scripts/e2e.sh` now creates a unique Compose project and volumes, submits a known accepted-ID set,
and verifies ingestion/gateway restart recovery, Postgres consumer retry, a durable poison-record
DLQ copy, alert-outbox recovery after a Kafka outage, and rejection of a TLS client without the
gateway certificate. It does not use the normal development project's containers or data.

`scripts/e2e.sh` now also freezes the broker with `docker pause` while an outbox produce is
in flight, manufacturing an ambiguous acknowledgement: the client times out without knowing
whether the broker persisted the request. The harness then proves the alert still arrives
at least once in `telemetry-alerts`, every copy shares one `MessageId` (the dashboard's
dedupe key), and Postgres still holds exactly one row per submitted ID. The harness does not
drive a real browser; render-side dedupe rests on the dashboard's bounded `remember()` set plus
`key={MessageId}`, covered by frontend lint and build.

## 2. Complete authentication and transport security

Compose now protects edge-to-ingestion gRPC with a private development CA, a gateway client
certificate, and an explicit SHA-256 certificate allowlist. This demonstrates the shape of
per-gateway revocation, but it is not a production certificate lifecycle.

Remaining work:

- sensor-to-edge traffic now uses mutual TLS: the dev CA issues one client certificate per
  simulated device (CN=EQ-N), each emulator replica loads only its shard over per-device
  HttpClient chains, and the gateway binds the certificate subject to the claimed equipment
  ID (403 otherwise). Proven by unit tests, a live TLS round trip, and e2e identity checks.
- Kafka client listeners now require mutual TLS plus authorization: the broker runs
  StandardAuthorizer with the broker and topic-setup admin as superusers, and
  kafka-init converges per-principal ACLs (ingestion writes events; diagnostics
  reads events and writes alerts/DLQ; web-api reads both with a prefixed group
  pattern for its per-host groups; the UI observer reads). Each .NET workload
  presents its own PEM identity, the topic setup and kafka-ui use
  password-protected PKCS12 stores, and the broker certificate carries clientAuth
  so the combined node can still talk to itself over its SSL inter-broker
  listener. The e2e harness proves a certless SSL client is rejected and that the
  read-only UI identity cannot produce. Still open: finer authorization,
  SASL as an alternative mechanism, and the EXTERNAL listener now also needs a
  client cert for host tools;
- Postgres now terminates TLS and enforces it: the server presents a dev-CA PEM
  identity (SAN `postgres`/`localhost`), pg_hba rejects plaintext TCP, and the
  diagnostics worker connects with `SSL Mode=VerifyFull` as a non-superuser
  `diagnostics` role converged by the postgres-init one-shot (idempotent, so it
  also upgrades pre-existing volumes). The role keeps DDL because the worker
  applies EF migrations at startup. Host EF tooling uses `make db-update`, which
  exports the dev CA via `make certs` and connects with VerifyFull; and
- Kafka client listeners now require mutual TLS;
- observability UIs are loopback-only; Grafana additionally requires the admin
  login (anonymous viewer is off). Prometheus, Alertmanager, MailHog, kafka-ui,
  and pgAdmin have no application authentication beyond loopback binding, and OTLP
  ingestion has none by protocol. Still open: per-user identity and secrets for
  those surfaces;
- the development CA lifecycle is now documented in [CA-lifecycle](docs/CA-lifecycle.md):
  stable leaves with 7-day renewal, restart-based rotation (live reload only for
  the gateway allowlist), no per-device sensor revocation, and `make certs-check`
  expiry auditing. Still open: replace the bootstrap with managed enrollment,
  renewal, rotation, revocation, and audit; and
- the gateway allowlist now reloads on a timer (`TransportSecurity:AllowlistReloadIntervalSeconds`,
  30s by default, 5s in the e2e project), so removing a fingerprint revokes that gateway
  without restarting ingestion; a malformed edit keeps the previous snapshot. Proven by unit
  tests (revocation, invalid-edit safety, CA rotation) and an e2e revocation drill.

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
- close the transport gap vs Compose before any security claim: the chart broker is still
  PLAINTEXT with no ACLs, the apps get no Kafka client identities, and PostgreSQL has no
  TLS or workload roles (`helm lint`/`helm template` and `make infra-validate` pass, which
  proves rendering, not deployment);
- run plan, apply, rollout, failure recovery, rollback, and destroy in a disposable account or
  cluster; and
- retain a reviewed promotion boundary before any persistent environment changes.

## 4. Calibrate and route observability alerts

Compose now provisions a checked-in Grafana dashboard, Prometheus alert rules, and named storage
for Prometheus, Loki, Tempo, and Grafana. The rules deliberately cover permanent sensor drops,
gateway backlog/rejections, diagnostics retries/DLQ failures, and the alert outbox.

The notification route now exists: Prometheus forwards firing alerts to Alertmanager, which
groups by alert and component and emails a local MailHog inbox (verified end to end with a
collector outage). Retention budgets are explicit: Prometheus two weeks or two gigabytes,
Loki and Tempo one week each. Every rule carries a `follow_up` annotation naming its concrete
next hop (quarantine view, DLQ topic, backlog panel, logs).

Measured so far (see [Alert-tuning](docs/Alert-tuning.md)): an ingestion outage pages the
operator inbox in 2m51s via `EdgeCloudUnreachable`, resolves with a drained queue on restore,
and a 6-row restart backlog clears without tripping `AlertOutboxBacklogged` — so those windows
stay as they are. The slow-burn and load-dependent rules (10m-deriv growth, p95/p99 lag,
histogram buckets, sensor-drop thresholds) are still engineering defaults: they need a
sustained load drill and a >15-minute outage, then a re-verified full firing drill an operator
follows from the alert through metrics, logs, traces, quarantine, and the DLQ.

## 5. Optional cleanup and measurement

- Rename `Industrial.Data.ML` to something like `Industrial.ML.DetectorBuilder`; it creates an
  online IID detector artifact and does not train a learned model.
- Evaluate .NET Aspire only if it removes concrete Compose/observability work without obscuring the
  store-and-forward architecture.
- Code-split the dashboard if its current single production JavaScript chunk becomes a practical
  load-time issue.
- Record a reproducible load-test result before making throughput or sub-second latency claims.
