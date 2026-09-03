# track: swe | csa | both. TODO-prefixed lines are not true yet.

projects:
  - id: telemetry
    name: "Cloud Infrastructure & Distributed Systems"
    context: "Personal Project"
    stack: ".NET 10, Kafka, Docker, ML.NET"
    bullets:
      # working set
      - id: arch
        track: both
        text: "Architected an event-driven telemetry platform using .NET 10, Apache Kafka, and Docker to ingest and process real-time sensor streams."
      - id: ml-worker
        track: both
        text: "Developed a background worker consuming Kafka events to run ML.NET anomaly detection and trigger automated AI diagnostics."
      - id: signalr
        track: both
        text: "Implemented a SignalR WebSocket bridge to stream live telemetry events directly to a React dashboard."
      - id: terraform
        track: csa
        text: "Drafted infrastructure specifications using Terraform (IaC) and defined GitOps deployment workflows via ArgoCD on Kubernetes (Helm, Istio)."
      - id: ai-pipeline
        track: both
        text: "Developed an AI pipeline combining ML.NET time-series anomaly detection with LLM diagnostics to automatically convert sensor failures into actionable corrective reports."
      - id: observability
        track: both
        text: "Configured distributed observability using OpenTelemetry, Prometheus, and Grafana to track microservice health, aggregate logs, and trace latency bottlenecks."
      - id: store-forward
        track: swe
        text: "Engineered an edge gateway with a durable SQLite WAL buffer (synchronous=FULL) to survive total cloud outages, shedding excess load via HTTP 429 backpressure to keep the buffer bounded."
      - id: idempotency
        track: swe
        text: "Designed the pipeline for at-least-once delivery over gRPC and Kafka, achieving effectively-once processing by minting UUIDv7 idempotency keys at the sensor origin and deduplicating in Postgres."
      - id: tracing
        track: swe
        text: "Implemented distributed W3C tracing, metrics, and logging across the pipeline using OpenTelemetry, aggregating into Prometheus, Tempo, and Loki for Grafana dashboards."
      - id: api-relay
        track: swe
        text: "Developed an ASP.NET Web API relaying Kafka events via a SignalR WebSocket bridge, pushing live updates to a React/Vite frontend."


      # pool
      - id: settlement
        track: swe
        text: "Eliminated data loss on ambiguous upload failures by deleting a buffered reading only after the cloud explicitly named its ID accepted or rejected, retrying everything left unnamed."
      - id: head-of-line
        track: swe
        text: "Implemented per-reading rejection at ingestion, quarantining a single malformed record at the edge instead of letting it block the upload queue behind it."
      - id: quarantine
        track: swe
        text: "Built a bounded SQLite quarantine table that preserves rejected readings with their original payload and reason code, written in the same transaction as the live-row delete so poison records stay auditable."
      - id: outbox
        track: swe
        text: "Guaranteed alert delivery with a transactional outbox, committing each detected anomaly and its alert in a single Postgres transaction and deduplicating publisher retries by message ID."
      - id: dlq
        track: swe
        text: "Added a Kafka dead-letter path that durably copies malformed records with their source partition and offset before committing, keeping bad payloads inspectable without stalling the consumer."
      - id: decoupling
        track: swe
        text: "Decoupled sensor acquisition from transmission behind a bounded channel so network latency cannot reduce the sample rate, instrumenting every intentionally dropped reading."
      - id: admission-concurrency
        track: swe
        text: "Made buffer admission an atomic reservation guarded by a mutation gate, so concurrent writers cannot exceed the depth ceiling or observe a half-applied state change."
      - id: event-time
        track: swe
        text: "Modeled event time and processing time separately so readings flushed after a 30-minute outage retain their true occurrence time, with sequence numbers exposing dropped ranges."
      - id: health
        track: both
        text: "Added liveness and dependency-aware readiness probes across all services, covering Kafka partition assignment, Postgres, and SQLite writability."
      - id: e2e-chaos
        track: swe
        text: "Wrote an isolated Docker failure-injection harness that kills ingestion, Postgres, and Kafka mid-run and asserts queue drain, exactly one row per accepted ID, offset rewind, and outbox recovery."
      - id: schema-evolution
        track: swe
        text: "Enforced Protobuf schema-evolution discipline on the gRPC contract with frozen field numbers and reserved tags, backed by a regression test that fails CI on a renumbered field."
      - id: input-validation
        track: swe
        text: "Aligned edge admission bounds with cloud-side validation so unprocessable input is refused at the device boundary, including malformed trace context caught before it reaches a Kafka header."
      - id: db-schema
        track: swe
        text: "Designed the Postgres schema around its access patterns, adding unique idempotency indexes, a composite index for pending-outbox scans, and an equipment/time index for detector warm-up."
      - id: kafka-keying
        track: both
        text: "Keyed Kafka events by EquipmentId so each machine's readings stay ordered on one partition, enabling per-equipment detector state while consumption scales across machines."
      - id: config
        track: csa
        text: "Bound every service's runtime configuration to validated typed options with fail-fast startup checks, so a missing deployment value stops the container instead of silently defaulting."
      - id: ci
        track: both
        text: "Set up GitHub Actions CI running a warnings-as-errors build, EF Core migration-drift detection, tests with coverage, frontend lint and build, Compose validation, and Helm chart lint and render."
      - id: reliability-boundary
        track: both
        text: "Defined an explicit reliability boundary — best-effort before the gateway's 202 Accepted, durably at-least-once after it — and proved it with regression tests, chaos targets, and an automated audit."


      # sharper variants of working-set lines
      - id: arch-alt
        track: both
        text: "Architected a 7-service event-driven telemetry platform using .NET 10, Apache Kafka, and Postgres, running as a 19-container Docker Compose stack with full observability."
      - id: store-forward-alt
        track: swe
        text: "Engineered an edge gateway buffering 500K readings in SQLite (WAL, synchronous=FULL) to survive total cloud outages, shedding overflow via HTTP 429 and Retry-After backpressure."
      - id: tracing-alt
        track: swe
        text: "Preserved W3C distributed tracing across a durable buffered hop by persisting trace context in SQLite and replaying it as span links, keeping a reading's trace intact through multi-hour outages."
      - id: api-relay-alt
        track: swe
        text: "Developed an ASP.NET Web API relaying Kafka events via a SignalR WebSocket bridge, giving each replica its own consumer group so every client receives the full live stream."
      - id: terraform-alt
        track: csa
        text: "Authored a Helm chart with Istio and External Secrets templates, validated by lint and render in CI, plus Terraform definitions for Kafka topics, ArgoCD, and Istio."


      # TODO
      - id: gitops
        track: both
        text: "TODO: Deployed microservices using Helm, ArgoCD (GitOps), and Istio service mesh, with full observability configured via OpenTelemetry, Prometheus, and Grafana."
      - id: arch-k8s
        track: both
        text: "TODO: Architected an event-driven telemetry platform using .NET 10, Apache Kafka, and Kubernetes to ingest and process real-time sensor streams."
      - id: terraform-implemented
        track: csa
        text: "TODO: Drafted infrastructure specifications using Terraform (IaC) and implemented GitOps deployment workflows via ArgoCD on Kubernetes (Helm, Istio)."
      - id: api-relay-subsecond
        track: swe
        text: "TODO: Developed an ASP.NET Web API relaying Kafka events via a SignalR WebSocket bridge, pushing sub-second updates to a React/Vite frontend."
      - id: mtls
        track: csa
        text: "TODO: Secured service-to-service traffic with mTLS, issuing a per-gateway client certificate so a single device can be revoked independently."
      - id: perf
        track: both
        text: "TODO: Sustained N readings/sec end to end at Xms p95 Kafka-to-dashboard latency (fill in real measured numbers)."

      # TODO: MLOps
      - id: ml-dataset
        track: swe
        text: "TODO: Generated a labeled telemetry dataset covering gradual degradation and sensor drift, snapshotted and versioned from Postgres for reproducible training runs."
      - id: ml-provenance
        track: swe
        text: "TODO: Persisted per-decision model provenance including score, p-value, model version, and warm-up length, making every anomaly decision auditable and reproducible."
      - id: ml-training
        track: both
        text: "TODO: Trained and evaluated a supervised anomaly model on labeled equipment telemetry, reporting precision and recall against a held-out set rather than a fixed-threshold detector."
      - id: mlops-pipeline
        track: both
        text: "TODO: Built an MLOps pipeline versioning datasets and model artifacts, shadow-testing each candidate against the live detector before promotion, with drift monitoring and automated rollback."
