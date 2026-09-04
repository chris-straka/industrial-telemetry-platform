# Security boundary

Docker Compose demonstrates mutual TLS only on the WAN-shaped edge-gateway to ingestion-api gRPC
hop. It does not make the whole local platform a zero-trust deployment.

| hop or surface | Compose protection | current limitation |
| --- | --- | --- |
| edge gateway -> ingestion gRPC | TLS plus required client certificate | development PKI; one simulated gateway identity |
| sensor -> edge receiver | mutual TLS, one client certificate per simulated device bound to its equipment ID | development PKI; 12 simulated device identities |
| applications -> Kafka | mutual TLS, one client certificate per workload plus admin/UI observer identities | no Kafka ACLs: identity is proven at the handshake, not authorization; development PKI |
| diagnostics -> Postgres | server-side TLS with VerifyFull, non-superuser workload role, plaintext TCP rejected by pg_hba | development passwords in Compose config; worker still needs DDL for startup migrations |
| applications -> OTel collector/backends | private Compose network | plaintext, unauthenticated OTLP/backend traffic |
| dashboard/operator tools | host ports bound to `127.0.0.1` | local-only exposure is not application authentication |

The explicit boundary matters: saying “the platform uses mTLS” without naming the protected hop
would overstate what this repository proves.

# Development PKI flow

`dev-pki-init` runs `scripts/create-dev-pki.sh` before the edge, ingestion, Postgres,
and Kafka services start. It creates separately mounted named volumes:

- an authority volume containing the private development CA;
- an ingestion volume containing the server PFX, public CA certificate, and allowed client
  fingerprints;
- an edge volume containing the gateway client PFX, gateway server PFX, and public CA
  certificate;
- a sensor volume containing one client PFX per simulated device plus the public CA
  certificate;
- a Kafka volume containing the broker server PKCS12, public CA certificate, JVM client
  properties, and the admin/UI observer client PKCS12 stores;
- a Kafka-clients volume containing one PEM client cert/key pair per .NET workload
  (ingestion, diagnostics, web-api), mounted into those containers but never into the
  broker; and
- a Postgres volume containing the database server certificate and private key in PEM form,
  mounted only into the database container.

The CA private key is never mounted into an application container. Each application sees only its
own leaf private key. Valid identities remain stable across normal Compose stops and restarts;
`docker compose down -v` intentionally deletes the PKI volumes and causes new identities to be
issued on the next start.

The ingestion certificate has server-auth usage and DNS names for `ingestion-api` and `localhost`.
The gateway certificate has client-auth usage and a development SPIFFE-style URI SAN. The current
script issues one gateway identity, but the ingestion allowlist is a set so the same policy can
represent multiple independently removable fingerprints.

# Validation on both ends

When `TransportSecurity:Enabled` is true, both applications fail startup if their required paths
are missing, and the edge refuses a non-HTTPS cloud URL.

The edge presents its PFX and accepts the ingestion server only when all of these checks pass:

- the TLS name matches the requested host;
- the leaf chains to the configured private CA using custom-root trust;
- it is an end-entity certificate with digital-signature key usage; and
- it carries the server-auth extended key usage.

Ingestion requires a client certificate at the TLS handshake, applies the corresponding private-CA
and client-auth checks, and then requires the certificate's SHA-256 fingerprint in
`allowed-client-sha256.txt`. Removing one fingerprint and restarting ingestion denies that client
without changing the trust decision for other allowlisted gateways. The isolated E2E harness also
checks that a TLS request without the gateway certificate is rejected.

The policy disables certificate downloads and does not consult CRLs or OCSP. The explicit
fingerprint list is the demonstrated revocation mechanism, and it is loaded at process startup—not
watched for live changes. Those are deliberate limits of this local example, not a complete device
certificate lifecycle.

# Kubernetes chart

The Helm chart has an opt-in `transportSecurity.enabled` mode that mounts separate pre-provisioned
Secrets for the ingestion and edge identities and configures the same HTTPS/mTLS application path.
The chart does not issue those certificates or create a production CA. Rendering that mode proves
the configuration shape, not enrollment, rotation, revocation, or successful deployment in a real
cluster.

# Production work still required

A production design still needs a managed issuer and enrollment flow, renewal before expiry,
auditable per-device revocation, workload identity for cloud services, Kafka authorization
(ACLs) on top of the handshake identity, Postgres plaintext prohibition plus
workload-specific credentials, protected telemetry backends and operator UIs, and secret
rotation that does not require values in Terraform state. Those tasks remain in `TODO.md`.
