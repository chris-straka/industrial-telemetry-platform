env                    = "Production"
github_repo_url        = "https://github.com/YOUR_ORG/IndustrialPlatform.git"
github_target_revision = "v0.1.0"
application_namespace  = "industrial-platform"

# Replace these example registry coordinates with immutable, published release references.
application_images = {
  edge_gateway       = "ghcr.io/YOUR_ORG/industrial-platform/edge-gateway:0.1.0"
  ingestion_api      = "ghcr.io/YOUR_ORG/industrial-platform/ingestion-api:0.1.0"
  diagnostics_worker = "ghcr.io/YOUR_ORG/industrial-platform/diagnostics-worker:0.1.0"
  sensor_emulator    = "ghcr.io/YOUR_ORG/industrial-platform/sensor-emulator:0.1.0"
  web_api            = "ghcr.io/YOUR_ORG/industrial-platform/web-api:0.1.0"
  web_dashboard      = "ghcr.io/YOUR_ORG/industrial-platform/web-dashboard:0.1.0"
}

# Broker endpoints are identifiers, not credentials. Inject SASL credentials through protected
# TF_VAR_kafka_sasl_* environment variables when the chosen cluster requires them.
kafka_bootstrap_servers = [
  "kafka-admin-1.prod.example:9093",
  "kafka-admin-2.prod.example:9093",
  "kafka-admin-3.prod.example:9093",
]
application_kafka_bootstrap_servers = [
  "kafka-client-1.prod.internal:9092",
  "kafka-client-2.prod.internal:9092",
  "kafka-client-3.prod.internal:9092",
]
kafka_tls_enabled     = true
kafka_skip_tls_verify = false
replication_factor    = 3
partitions            = 12

chart_postgresql_enabled          = true
install_external_secrets_operator = true
external_secret = {
  enabled                               = true
  secret_store_name                     = "aws-secretsmanager"
  secret_store_kind                     = "ClusterSecretStore"
  gemini_api_key_remote_key             = "industrial-platform/prod/gemini-api-key"
  postgres_connection_string_remote_key = "industrial-platform/prod/postgres-connection-string"
  postgres_password_remote_key          = "industrial-platform/prod/postgres-password"
}

install_istio                   = false
enable_platform_istio_resources = false
