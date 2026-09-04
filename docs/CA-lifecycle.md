# Development CA lifecycle

This is the operations manual for the private CA in `scripts/create-dev-pki.sh`.
It is a local-development lifecycle, not a production PKI: there is no managed
issuer, no OCSP/CRL, and every secret is a checked-in placeholder. What it does
give you is deterministic, auditable behavior for issuance, renewal, revocation,
and expiry — each proven by something that runs.

## Issuance and renewal

`dev-pki-init` runs before everything that needs identity. It keeps a leaf
stable while it verifies against the current CA **and** stays valid for more
than 7 days (`openssl ... -checkend 604800`); otherwise it reissues. Ordinary
restarts therefore keep identities, and upcoming expiry renews itself on the
next `up`.

The CA itself (10 years) is created once per volume lifetime. `down -v`
destroys it along with everything else, and the next start bootstraps a fresh
one.

## What a rotation touches

Leaf rotation is a restart, because every consumer reads its identity at
startup:

| rotated | picks it up | how |
| --- | --- | --- |
| gateway / ingestion / sensor / Kafka / Postgres leaf | that service | `docker compose up -d <service>` recreates it; Postgres re-stages its pair, the broker re-reads its keystore |
| gateway allowlist entry | ingestion | live reload, no restart (proven by the e2e revocation drill) |
| whole CA | everything | reissue cascades: the freshness check verifies each leaf against the current `ca.crt`, so a new CA reissues every leaf on the next `up`; the gateway fingerprint file regenerates with it |

CA rotation is the one destructive case: old and new leaves do not chain to a
common root during the changeover, so rotate with the stack down, not rolling.
Client data is unaffected — no cert-bound state exists outside the ingestion
allowlist, which regenerates.

## Revocation

Per-gateway revocation is the ingestion allowlist
(`TransportSecurity__AllowedClientFingerprintsPath`), reloaded on a timer; the
e2e harness proves removal denies uploads without a restart and restore drains
the retained row. There is deliberately no CRL/OCSP distribution point.

There is **no** per-device sensor revocation: the gateway binds the presented
certificate to the claimed equipment ID at the handshake, but a compromised
device certificate cannot be individually revoked short of rotating the CA.
That gap is acceptable for 12 simulated devices and would be the first thing a
real deployment fixes.

## Audit

`make certs-check` (optionally `WARN_DAYS=N`) reports days-to-expiry for the CA
and every leaf from the same volumes the services mount, and fails when
anything expires within the window. Run it in CI or before a demo, not after
an outage.
