locals {
  platform_helm_parameters = concat(
    [
      {
        name        = "platform.environment"
        value       = var.env
        forceString = true
      },
      {
        name        = "platform.kafka.bootstrapServers"
        value       = join(",", var.application_kafka_bootstrap_servers)
        forceString = true
      },
      {
        name        = "platform.kafka.eventsTopic"
        value       = var.events_topic
        forceString = true
      },
      {
        name        = "platform.kafka.alertsTopic"
        value       = var.alerts_topic
        forceString = true
      },
      {
        name        = "platform.kafka.deadLetterTopic"
        value       = var.dead_letter_topic
        forceString = true
      },
      {
        name        = "platform.applicationSecret.name"
        value       = var.application_secret_name
        forceString = true
      },
      {
        name        = "images.edge-gateway"
        value       = var.application_images.edge_gateway
        forceString = true
      },
      {
        name        = "images.ingestion-api"
        value       = var.application_images.ingestion_api
        forceString = true
      },
      {
        name        = "images.diagnostics-worker"
        value       = var.application_images.diagnostics_worker
        forceString = true
      },
      {
        name        = "images.sensor-emulator"
        value       = var.application_images.sensor_emulator
        forceString = true
      },
      {
        name        = "images.web-api"
        value       = var.application_images.web_api
        forceString = true
      },
      {
        name        = "images.web-dashboard"
        value       = var.application_images.web_dashboard
        forceString = true
      },
      {
        name        = "kafka.enabled"
        value       = "false"
        forceString = false
      },
      {
        name        = "postgresql.enabled"
        value       = tostring(var.chart_postgresql_enabled)
        forceString = false
      },
      {
        name        = "externalSecret.enabled"
        value       = tostring(var.external_secret.enabled)
        forceString = false
      },
      {
        name        = "istio.enabled"
        value       = tostring(var.enable_platform_istio_resources)
        forceString = false
      },
      {
        name        = "istio.sidecarInjection"
        value       = tostring(var.enable_platform_istio_resources)
        forceString = false
      },
    ],
    var.external_secret.enabled ? [
      {
        name        = "externalSecret.apiVersion"
        value       = "external-secrets.io/v1"
        forceString = true
      },
      {
        name        = "externalSecret.secretStoreRef.name"
        value       = var.external_secret.secret_store_name
        forceString = true
      },
      {
        name        = "externalSecret.secretStoreRef.kind"
        value       = var.external_secret.secret_store_kind
        forceString = true
      },
      {
        name        = "externalSecret.remoteRefs.geminiApiKey.key"
        value       = var.external_secret.gemini_api_key_remote_key
        forceString = true
      },
      {
        name        = "externalSecret.remoteRefs.postgresConnectionString.key"
        value       = var.external_secret.postgres_connection_string_remote_key
        forceString = true
      },
      {
        name        = "externalSecret.remoteRefs.postgresPassword.key"
        value       = var.external_secret.postgres_password_remote_key
        forceString = true
      },
    ] : []
  )
}

resource "kubernetes_namespace_v1" "argocd" {
  metadata {
    name = var.argocd_namespace
  }
}

resource "helm_release" "argocd" {
  name       = "argocd"
  repository = "https://argoproj.github.io/argo-helm"
  chart      = "argo-cd"
  version    = var.argocd_chart_version
  namespace  = kubernetes_namespace_v1.argocd.metadata[0].name

  atomic          = true
  cleanup_on_fail = true
  timeout         = 600
  wait            = true

  set = [
    {
      name  = "server.service.type"
      value = var.argocd_server_service_type
      type  = "string"
    }
  ]
}

# kubectl_manifest can create the Argo Application after the Helm release installs its CRD. The
# generated manifest contains deployment references only; application secret values never enter
# Terraform configuration, plans, state, or the Argo Application object.
resource "kubectl_manifest" "industrial_platform_app" {
  depends_on = [
    helm_release.argocd,
    helm_release.external_secrets,
    helm_release.istio_ingress,
    kafka_topic.telemetry_alerts,
    kafka_topic.telemetry_events,
    kafka_topic.telemetry_events_dead_letter,
  ]

  yaml_body = yamlencode({
    apiVersion = "argoproj.io/v1alpha1"
    kind       = "Application"
    metadata = {
      name      = "industrial-platform"
      namespace = kubernetes_namespace_v1.argocd.metadata[0].name
    }
    spec = {
      project = "default"
      source = {
        repoURL        = var.github_repo_url
        targetRevision = var.github_target_revision
        path           = "chart"
        helm = {
          valueFiles = ["values.yaml"]
          parameters = local.platform_helm_parameters
        }
      }
      destination = {
        server    = "https://kubernetes.default.svc"
        namespace = var.application_namespace
      }
      syncPolicy = {
        automated = {
          prune    = true
          selfHeal = true
        }
        syncOptions = ["CreateNamespace=true"]
      }
    }
  })
}
