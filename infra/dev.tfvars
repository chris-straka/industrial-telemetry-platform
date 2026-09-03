env                    = "Development"
github_repo_url        = "https://github.com/YOUR_ORG/IndustrialPlatform.git"
github_target_revision = "main"
application_namespace  = "industrial-platform-dev"

application_images = {
  edge_gateway       = "ghcr.io/YOUR_ORG/industrial-platform/edge-gateway:dev"
  ingestion_api      = "ghcr.io/YOUR_ORG/industrial-platform/ingestion-api:dev"
  diagnostics_worker = "ghcr.io/YOUR_ORG/industrial-platform/diagnostics-worker:dev"
  sensor_emulator    = "ghcr.io/YOUR_ORG/industrial-platform/sensor-emulator:dev"
  web_api            = "ghcr.io/YOUR_ORG/industrial-platform/web-api:dev"
  web_dashboard      = "ghcr.io/YOUR_ORG/industrial-platform/web-dashboard:dev"
}

# The first endpoint is the TLS administrative listener reachable from Terraform. Platform pods
# currently support only the separate internal PLAINTEXT listener.
kafka_bootstrap_servers             = ["kafka-admin.dev.internal:9093"]
application_kafka_bootstrap_servers = ["kafka-client.dev.svc.cluster.local:9092"]
kafka_tls_enabled                   = true
kafka_skip_tls_verify               = false
replication_factor                  = 1
partitions                          = 6

chart_postgresql_enabled          = true
install_external_secrets_operator = false
external_secret = {
  enabled                               = false
  secret_store_name                     = ""
  secret_store_kind                     = "ClusterSecretStore"
  gemini_api_key_remote_key             = ""
  postgres_connection_string_remote_key = ""
  postgres_password_remote_key          = ""
}

install_istio                   = false
enable_platform_istio_resources = false
