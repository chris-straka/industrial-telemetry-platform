# Docker failure/recovery E2E

Run from anywhere in the repository:

```sh
./scripts/e2e.sh
```

The harness builds three application images, starts only its own isolated Compose project, and
checks these reliability seams:

1. ingestion's TLS endpoint refuses clients without an allowlisted gateway certificate;
2. the edge queues readings while ingestion is stopped;
3. the SQLite queue survives an edge restart and drains after ingestion recovers;
4. Postgres contains exactly one row per submitted `MessageId`;
5. Diagnostics rewinds and later persists a reading consumed during a Postgres outage;
6. malformed Kafka JSON is durably copied to `telemetry-events-dlq` before its source offset is
   committed; and
7. a pending alert outbox row survives a Kafka outage and publishes after broker recovery.

Docker with the Compose plugin must be running. The first run may take several minutes while it
pulls base images and builds the .NET services. Set `E2E_SKIP_BUILD=1` to reuse previously built E2E
images, or increase `E2E_STARTUP_TIMEOUT_SECONDS` on a slow machine.

Every run gets a unique project name and therefore unique containers, network, and named volumes.
It publishes no host ports and never invokes `down` against the normal development project. An
exit trap prints bounded logs on failure, then removes only the current E2E project's containers
and volumes.
