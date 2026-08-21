# Agritech

Cloud-hosted .NET Core microservice app that ingests simulated sensor data (e.g., from industrial devices) and flags issues.

Data flow diagram

```
                         sensor-emulator
                               │ (HTTP POST)
                               ▼
                         ingestion-api
                               │ (Produces to Kafka: telemetry-events)
                               ▼
   ┌───────────────────────── Kafka ────────────┐
   │                           ▲                │
   ▼                           │                ▼
diagnostics-worker             │             web-api
 ├─ Consumes telemetry-events  │              ├─ Consumes telemetry-events
 ├─ Runs ML Anomaly Check      │              ├─ Consumes telemetry-alerts
 ├─ Saves to Postgres          │              └─ Relays live data via SignalR
 ├─ Calls Gemini AI            │                             │
 └─ Produces telemetry-alerts──┘                             ▼
                                                       web-dashboard
```

# Install

- [tilt](https://tilt.dev/)
- [Make](https://www.gnu.org/software/make/)
- [docker](https://www.docker.com/)
- [.NET](https://dotnet.microsoft.com/en-us/download)
- [Node](https://nodejs.org/en)
- [Terraform](https://developer.hashicorp.com/terraform/install)

# Key Features Include:

Data Ingestion Pipeline (C#/.NET Core & SQL): Create REST APIs to receive continuous telemetry/sensor data and store it in a SQL database.

AI Diagnostic Service (C# & AI API): Build a backend service that detects anomalies or error codes in the incoming data and uses an AI API to generate troubleshooting steps for the equipment operator.
