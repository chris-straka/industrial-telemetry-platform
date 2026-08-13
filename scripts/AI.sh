{
  for file in \
    .github/workflows/terraform-apply.yaml \
    .github/workflows/terraform-plan.yaml \
    \
    chart/templates/custom-apps.yaml \
    chart/templates/external-secret.yaml \
    chart/templates/istio.yaml \
    chart/Chart.yaml \
    chart/values.yaml \
    \
    infra/argocd.tf \
    infra/dev.tfvars \
    infra/external-secrets.tf \
    infra/istio.tf \
    infra/main.tf \
    infra/prod.tfvars \
    infra/providers.tf \
    infra/variables.tf \
    \
    monitoring/grafana-datasources.yaml \
    monitoring/loki-config.yaml \
    monitoring/otel-collector-config.yaml \
    monitoring/prometheus.yaml \
    monitoring/tempo.yaml \
    \
    pgadmin/pgadmin-servers.json \
    \
    src/Industrial.Data.ML/Industrial.Data.ML.csproj \
    src/Industrial.Data.ML/Program.cs \
    \
    src/Industrial.Diagnostics.Worker/Features/Diagnostics/ML/ModelEngine.cs \
    src/Industrial.Diagnostics.Worker/Features/Diagnostics/TelemetryConsumerWorker.cs \
    src/Industrial.Diagnostics.Worker/Features/Diagnostics/TelemetryReading.cs \
    src/Industrial.Diagnostics.Worker/Infrastructure/Data/AppDbContext.cs \
    src/Industrial.Diagnostics.Worker/Migrations/20260428231306_First.cs \
    src/Industrial.Diagnostics.Worker/Migrations/20260428231306_First.Designer.cs \
    src/Industrial.Diagnostics.Worker/Migrations/AppDbContextModelSnapshot.cs \
    src/Industrial.Diagnostics.Worker/Properties/launchSettings.json \
    src/Industrial.Diagnostics.Worker/appsettings.Development.json \
    src/Industrial.Diagnostics.Worker/appsettings.json \
    src/Industrial.Diagnostics.Worker/Dockerfile \
    src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.csproj \
    src/Industrial.Diagnostics.Worker/Industrial.Diagnostics.Worker.http \
    src/Industrial.Diagnostics.Worker/Program.cs \
    \
    src/Industrial.Ingestion.Api/Features/Ingestion/IngestTelemetryEndpoint.cs \
    src/Industrial.Ingestion.Api/Properties/launchSettings.json \
    src/Industrial.Ingestion.Api/appsettings.Development.json \
    src/Industrial.Ingestion.Api/appsettings.json \
    src/Industrial.Ingestion.Api/Industrial.Ingestion.Api.csproj \
    src/Industrial.Ingestion.Api/Industrial.Ingestion.Api.http \
    src/Industrial.Ingestion.Api/Dockerfile \
    src/Industrial.Ingestion.Api/Program.cs \
    \
    src/Industrial.Sensor.Emulator/Dockerfile \
    src/Industrial.Sensor.Emulator/Industrial.Sensor.Emulator.csproj \
    src/Industrial.Sensor.Emulator/Program.cs \
    \
    src/Industrial.Web.Api/Dockerfile \
    src/Industrial.Web.Api/Industrial.Web.Api.csproj \
    src/Industrial.Web.Api/Program.cs \
    \
    src/Industrial.Web.Dashboard/src/features/dashboard/AlertsList.tsx \
    src/Industrial.Web.Dashboard/src/features/dashboard/TelemetryChart.tsx \
    src/Industrial.Web.Dashboard/src/hooks/useTelemetry.ts \
    src/Industrial.Web.Dashboard/src/types/telemetry.ts \
    src/Industrial.Web.Dashboard/src/App.tsx \
    src/Industrial.Web.Dashboard/src/App.css \
    src/Industrial.Web.Dashboard/src/main.tsx \
    src/Industrial.Web.Dashboard/src/index.css \
    src/Industrial.Web.Dashboard/.prettierrc \
    src/Industrial.Web.Dashboard/Dockerfile \
    src/Industrial.Web.Dashboard/eslint.config.js \
    src/Industrial.Web.Dashboard/package.json \
    src/Industrial.Web.Dashboard/tsconfig.app.json \
    src/Industrial.Web.Dashboard/tsconfig.json \
    src/Industrial.Web.Dashboard/tsconfig.node.json \
    src/Industrial.Web.Dashboard/vite.config.ts \
    \
    docker-compose.yaml \
    Makefile \
    Tiltfile \
    prod.Tiltfile \
    \
    ; do

    # Determine syntax highlighting language
    ext="text"
    case "$file" in
      *.cs) ext="csharp" ;;
      *.yml|*.yaml) ext="yaml" ;;
      Makefile) ext="makefile" ;;
      *.json) ext="json" ;;
    esac

    # Print header and code block start
    printf "\n### File: %s\n\`\`\`%s\n" "$file" "$ext"
    # Cat the file content
    cat "$file" 2>/dev/null
    # Close the code block
    printf "\n\`\`\`\n"
  done
} | pbcopy && echo "All files copied to clipboard"
