# IndustrialPlatform Helm chart

This is a render-validated Kubernetes prototype for the application topology. It is not a
production deployment bundle: image publishing, an OTLP Collector, TLS/mTLS, authentication,
storage-class selection, and secret-store installation are intentionally left to the operator.

Before installation, create the Secret named by `platform.applicationSecret.name` with:

- `gemini-api-key`
- `postgres-password`
- `postgres-connection-string`, using the same password and the dependency Service host
  `industrial-platform-postgresql`

Alternatively, install External Secrets Operator and a compatible SecretStore, then enable and
configure `externalSecret`. The chart contains no literal application or database credentials.

The edge gateway is a StatefulSet with one durable SQLite PVC per pod. The sensor emulator is a
single-replica Deployment because `Emulator__ReplicaId` defines equipment ownership; demonstrate
additional ranges as separately configured releases rather than scaling that Deployment.

Validate the chart with:

```sh
helm dependency build chart
helm lint chart
helm template industrial-platform chart --namespace industrial-platform
```

Kafka and PostgreSQL are enabled by default. Kafka topic provisioning is a regular Job so installs
using `--wait` cannot deadlock on application readiness before the topics exist. The observability
backend dependencies and optional Istio/ExternalSecret resources are disabled until explicitly
configured.
