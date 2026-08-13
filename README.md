# Agritech

Cloud-hosted .NET Core microservice app that ingests simulated sensor data (e.g., from industrial devices) and flags issues.

```sh
# Train the model on static data
make train
make dev
make front
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
