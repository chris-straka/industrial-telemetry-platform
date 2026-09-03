variable "env" {
  type        = string
  description = "ASP.NET Core environment name supplied to the platform chart."

  validation {
    condition     = contains(["Development", "Staging", "Production"], var.env)
    error_message = "env must be Development, Staging, or Production."
  }
}

variable "kubeconfig_path" {
  type        = string
  description = "Path to a kubeconfig for the existing target cluster."
  default     = "~/.kube/config"
}

variable "kubeconfig_context" {
  type        = string
  description = "Optional kubeconfig context. Null uses the kubeconfig's current context."
  default     = null
  nullable    = true
}

variable "github_repo_url" {
  type        = string
  description = "Git repository Argo CD will reconcile."

  validation {
    condition     = can(regex("^(https://|ssh://|git@)", var.github_repo_url))
    error_message = "github_repo_url must be an HTTPS or SSH Git URL."
  }
}

variable "github_target_revision" {
  type        = string
  description = "Branch or immutable tag Argo CD will reconcile."
  default     = "main"
}

variable "application_namespace" {
  type        = string
  description = "Namespace into which Argo CD deploys IndustrialPlatform."
  default     = "industrial-platform"
}

variable "argocd_namespace" {
  type        = string
  description = "Namespace in which this stack installs Argo CD."
  default     = "argocd"
}

variable "argocd_chart_version" {
  type        = string
  description = "Pinned argo-cd Helm chart version."
  default     = "9.5.17"
}

variable "argocd_server_service_type" {
  type        = string
  description = "Argo CD API service exposure. ClusterIP is the safe default."
  default     = "ClusterIP"

  validation {
    condition     = contains(["ClusterIP", "NodePort", "LoadBalancer"], var.argocd_server_service_type)
    error_message = "argocd_server_service_type must be ClusterIP, NodePort, or LoadBalancer."
  }
}

variable "install_external_secrets_operator" {
  type        = bool
  description = "Whether Terraform installs External Secrets Operator into the existing cluster."
  default     = false
}

variable "external_secrets_chart_version" {
  type        = string
  description = "Pinned External Secrets Operator Helm chart version."
  default     = "2.8.0"
}

variable "external_secret" {
  description = "Non-secret references used by the application chart's ExternalSecret."
  type = object({
    enabled                               = bool
    secret_store_name                     = string
    secret_store_kind                     = string
    gemini_api_key_remote_key             = string
    postgres_connection_string_remote_key = string
    postgres_password_remote_key          = string
  })

  validation {
    condition = !var.external_secret.enabled || alltrue([
      length(trimspace(var.external_secret.secret_store_name)) > 0,
      contains(["SecretStore", "ClusterSecretStore"], var.external_secret.secret_store_kind),
      length(trimspace(var.external_secret.gemini_api_key_remote_key)) > 0,
      length(trimspace(var.external_secret.postgres_connection_string_remote_key)) > 0,
      length(trimspace(var.external_secret.postgres_password_remote_key)) > 0,
    ])
    error_message = "Enabled external_secret configuration requires a store and all three remote keys."
  }
}

variable "application_secret_name" {
  type        = string
  description = "Name of the pre-provisioned or externally synchronized application Secret."
  default     = "industrial-platform-secrets"
}

variable "application_images" {
  description = "Full image references deployed by the application chart. Use immutable tags/digests for production."
  type = object({
    edge_gateway       = string
    ingestion_api      = string
    diagnostics_worker = string
    sensor_emulator    = string
    web_api            = string
    web_dashboard      = string
  })

  validation {
    condition = alltrue([
      for image in values(var.application_images) : length(trimspace(image)) > 0
    ])
    error_message = "Every application image reference must be non-empty."
  }
}

variable "chart_postgresql_enabled" {
  type        = bool
  description = "Whether the platform chart installs its PostgreSQL dependency."
  default     = true
}

variable "install_istio" {
  type        = bool
  description = "Whether Terraform installs Istio into the existing cluster."
  default     = false
}

variable "enable_platform_istio_resources" {
  type        = bool
  description = "Whether the platform chart creates its Istio Gateway and VirtualService."
  default     = false
}

variable "istio_chart_version" {
  type        = string
  description = "Pinned Istio base, control-plane, and gateway chart version."
  default     = "1.30.4"
}

variable "kafka_bootstrap_servers" {
  type        = list(string)
  description = "Kafka administrative endpoints reachable by the Terraform runner."

  validation {
    condition = length(var.kafka_bootstrap_servers) > 0 && alltrue([
      for broker in var.kafka_bootstrap_servers : can(regex("^[^:,[:space:]]+:[0-9]+$", broker))
    ])
    error_message = "kafka_bootstrap_servers must contain one or more host:port entries."
  }
}

variable "application_kafka_bootstrap_servers" {
  type        = list(string)
  description = "Kafka client endpoints rendered into the platform pods. Applications currently use PLAINTEXT."

  validation {
    condition = length(var.application_kafka_bootstrap_servers) > 0 && alltrue([
      for broker in var.application_kafka_bootstrap_servers : can(regex("^[^:,[:space:]]+:[0-9]+$", broker))
    ])
    error_message = "application_kafka_bootstrap_servers must contain one or more host:port entries."
  }
}

variable "kafka_tls_enabled" {
  type        = bool
  description = "Enable TLS for Terraform's Kafka administrative connection."
  default     = true
}

variable "kafka_skip_tls_verify" {
  type        = bool
  description = "Disable Kafka certificate verification. Keep false outside disposable local clusters."
  default     = false
}

variable "kafka_timeout_seconds" {
  type        = number
  description = "Timeout used by the Kafka provider."
  default     = 30

  validation {
    condition     = var.kafka_timeout_seconds >= 1 && var.kafka_timeout_seconds <= 300
    error_message = "kafka_timeout_seconds must be between 1 and 300."
  }
}

variable "kafka_sasl_mechanism" {
  type        = string
  description = "Optional Kafka SASL mechanism (for example scram-sha512 or aws-iam)."
  default     = null
  nullable    = true
}

variable "kafka_sasl_username" {
  type        = string
  description = "Optional Kafka SASL username supplied at runtime."
  default     = null
  nullable    = true
  sensitive   = true
}

variable "kafka_sasl_password" {
  type        = string
  description = "Optional Kafka SASL password supplied at runtime."
  default     = null
  nullable    = true
  sensitive   = true
}

variable "kafka_sasl_aws_region" {
  type        = string
  description = "Optional AWS region when kafka_sasl_mechanism is aws-iam."
  default     = null
  nullable    = true
}

variable "kafka_sasl_aws_role_arn" {
  type        = string
  description = "Optional role assumed by the Kafka provider when using aws-iam."
  default     = null
  nullable    = true
}

variable "events_topic" {
  type        = string
  description = "Telemetry event topic shared by Terraform and the platform chart."
  default     = "telemetry-events"
}

variable "alerts_topic" {
  type        = string
  description = "Telemetry alert topic shared by Terraform and the platform chart."
  default     = "telemetry-alerts"
}

variable "dead_letter_topic" {
  type        = string
  description = "Diagnostics poison-record topic shared by Terraform and the platform chart."
  default     = "telemetry-events-dlq"
}

variable "replication_factor" {
  type        = number
  description = "Kafka topic replication factor; the target cluster must have at least this many brokers."
  default     = 1

  validation {
    condition     = var.replication_factor >= 1
    error_message = "replication_factor must be at least one."
  }
}

variable "partitions" {
  type        = number
  description = "Partition count for each platform topic."
  default     = 6

  validation {
    condition     = var.partitions >= 1
    error_message = "partitions must be at least one."
  }
}
