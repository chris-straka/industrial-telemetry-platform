# From the "Store-Forward" bullet:

- "You mentioned using a SQLite WAL buffer with `synchronous=FULL`. Why SQLite over just writing to a local text file? What exactly does Write-Ahead Logging (WAL) do for you if the power goes out?"

- "How did you implement HTTP 429 backpressure? How did the client know to back off and retry?"

# From the "Idempotency" bullet:

- "You claim effectively-once processing. Kafka only guarantees at-least-once. How exactly did you guarantee exactly/effectively once?"

- "Why did you use UUIDv7 for idempotency keys instead of standard UUIDv4?"

# From the "API Relay & SignalR" bullets:

- "SignalR holds open WebSocket connections. What happens to the telemetry stream if the ASP.NET server restarts or scales up? How do clients reconnect?"

- "How did you route Kafka events to specific SignalR clients?"

# From the "Observability & GitOps" bullets:

- "How did you pass OpenTelemetry trace IDs from the gRPC origin, through Kafka, and into the ML worker?"

- "How does ArgoCD differ from traditional push-based CI/CD pipelines like GitHub Actions?"
