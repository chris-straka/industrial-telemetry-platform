terraform {
  required_version = "~> 1.16.0"

  # Supply bucket, key, and region with -backend-config. Keeping account-specific values out of
  # this block lets the same configuration target separate state stores without committing them.
  backend "s3" {
    encrypt      = true
    use_lockfile = true
  }

  required_providers {
    helm = {
      source  = "hashicorp/helm"
      version = "3.2.0"
    }
    kafka = {
      source  = "Mongey/kafka"
      version = "0.13.1"
    }
    kubectl = {
      source  = "gavinbunney/kubectl"
      version = "1.19.0"
    }
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "3.2.1"
    }
  }
}

# This stack configures an existing cluster. Authentication comes from the selected kubeconfig;
# it deliberately does not pretend to create an EKS cluster that is absent from this repository.
provider "kubernetes" {
  config_path    = pathexpand(var.kubeconfig_path)
  config_context = var.kubeconfig_context
}

provider "helm" {
  kubernetes = {
    config_path    = pathexpand(var.kubeconfig_path)
    config_context = var.kubeconfig_context
  }
}

provider "kubectl" {
  config_path      = pathexpand(var.kubeconfig_path)
  config_context   = var.kubeconfig_context
  load_config_file = true
}

provider "kafka" {
  bootstrap_servers = var.kafka_bootstrap_servers
  timeout           = var.kafka_timeout_seconds
  tls_enabled       = var.kafka_tls_enabled
  skip_tls_verify   = var.kafka_skip_tls_verify

  # Optional credentials are injected at runtime (for example with TF_VAR_* in the protected
  # apply environment), never committed in a tfvars file.
  sasl_mechanism    = var.kafka_sasl_mechanism
  sasl_username     = var.kafka_sasl_username
  sasl_password     = var.kafka_sasl_password
  sasl_aws_region   = var.kafka_sasl_aws_region
  sasl_aws_role_arn = var.kafka_sasl_aws_role_arn
}
