variable "env" {
  type        = string
  description = "The environment (e.g. dev, prod)"
}

variable "github_repo_url" {
  type        = string
  description = "URL of your Git repository"
}

variable "github_target_revision" {
  type        = string
  description = "Branch or tag to deploy (e.g. main)"
  default     = "main"
}

variable "argocd_chart_version" {
  type        = string
  description = "Version of the ArgoCD Helm chart to use"
  default     = "7.3.11" # Always pin versions in production!
}

variable "gemini_api_key" {
  type        = string
  description = "API Key for Gemini"
  sensitive   = true
}

variable "bootstrap_servers" {
  type        = string
  description = "host:port for Kafka"
}

variable "replication_factor" {
  type    = number
  default = 1
}

variable "partitions" {
  type    = number
  default = 6
}
