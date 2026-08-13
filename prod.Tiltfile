docker_build('web-api', '.', dockerfile='src/Industrial.Web.Api/Dockerfile')
docker_build('ingestion-api', '.', dockerfile='src/Industrial.Ingestion.Api/Dockerfile')
docker_build('diagnostics-worker', '.', dockerfile='src/Industrial.Diagnostics.Worker/Dockerfile')
docker_build('sensor-emulator', '.', dockerfile='src/Industrial.Sensor.Emulator/Dockerfile')
docker_build('web-dashboard', 'src/Industrial.Web.Dashboard', dockerfile='src/Industrial.Web.Dashboard/Dockerfile')

helm_resource('industrial-platform', 'chart/')
