# Build images (2nd arg is dockerfile build context, usually dotnet restore)
docker_build('ingestion-api', '.', dockerfile='src/Industrial.Ingestion.Api/Dockerfile')
docker_build('diagnostics-worker', '.', dockerfile='src/Industrial.Diagnostics.Worker/Dockerfile')
docker_build('sensor-emulator', '.', dockerfile='src/Industrial.Sensor.Emulator/Dockerfile')
docker_build('web-api', '.', dockerfile='src/Industrial.Web.Api/Dockerfile')

# Development uses node:24, production uses my dockerfile
# Dashboard only needs the package.json for the build context
# docker_build('web-dashboard', 'src/Industrial.Web.Dashboard', dockerfile='src/Industrial.Web.Dashboard/Dockerfile')

# Spin up Postgres and Kafka
docker_compose('docker-compose.yaml')
