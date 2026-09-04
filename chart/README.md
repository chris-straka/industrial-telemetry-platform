# IndustrialPlatform Helm chart

This is a render-validated Kubernetes prototype for the application topology. It is not a
production deployment bundle: image publishing, an OTLP Collector, authentication,
storage-class selection, and secret-store installation are intentionally left to the operator.
The chart can configure edge-to-ingestion mTLS, but certificate issuance and rotation remain
operator responsibilities.

Before installation, create the Secret named by `platform.applicationSecret.name` with:

- `gemini-api-key`
- `postgres-password`
- `postgres-connection-string`, using the same password and the dependency Service host
  `industrial-platform-postgresql`

Alternatively, install External Secrets Operator and a compatible SecretStore, then enable and
configure `externalSecret`. The chart contains no literal application or database credentials.

## Edge-to-ingestion mTLS

Set `transportSecurity.enabled=true` and provision two Kubernetes Secrets. The Secret names and
key names are configurable under `transportSecurity`; the defaults below describe the required
contents:

- the ingestion Secret contains `server.pfx`, `server-pfx-password`, `ca.crt`, and
  `allowed-client-sha256.txt`;
- the edge gateway Secret contains `client.pfx`, `client-pfx-password`, and `ca.crt`.

`allowed-client-sha256.txt` contains one permitted client-certificate SHA-256 fingerprint per
line. The server certificate must be valid for the ingestion Service DNS name used by
`Cloud__ApiUrl`. Each PFX password is read directly from its Secret rather than placed in values.
The chart projects only the certificate files needed by each workload, read-only, and never mounts
the server private key into the edge gateway or the client private key into ingestion.

For example, after creating Secrets named `industrial-ingestion-mtls` and
`industrial-edge-mtls`, enable the mode with:

```yaml
transportSecurity:
  enabled: true
  ingestion:
    secretName: industrial-ingestion-mtls
  edgeGateway:
    secretName: industrial-edge-mtls
```

This changes only the internal gRPC endpoint on port 8081 to HTTPS with required client
certificates. REST startup, liveness, and readiness probes remain HTTP on port 8080.

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

Known gaps vs the Compose deployment: the chart broker is PLAINTEXT with no authorizer or
ACLs, the applications get no Kafka client identities, and PostgreSQL has no TLS or workload
roles. Do not present an install of this chart as carrying the transport posture proven by
`scripts/e2e.sh` until those are closed (see TODO.md).
