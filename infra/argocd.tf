resource "kubernetes_namespace" "argocd" {
  metadata {
    name = "argocd"
  }
}

resource "helm_release" "argocd" {
  name       = "argocd"
  repository = "https://argoproj.github.io/argo-helm"
  chart      = "argo-cd"
  version    = var.argocd_chart_version
  namespace  = kubernetes_namespace.argocd.metadata[0].name

  set = [ {
    name = "server.service.type",
    value = "LoadBalancer"
  } ]
}

# Use kubectl_manifest to avoid Terraform crashing on unknown CRDs
resource "kubectl_manifest" "industrial_platform_app" {
  depends_on = [helm_release.argocd]

  yaml_body = <<-YAML
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: industrial-platform
  namespace: argocd
spec:
  project: default
  source:
    repoURL: ${var.github_repo_url}
    targetRevision: ${var.github_target_revision}
    path: chart
    helm:
      valueFiles:
        - values.yaml
      parameters:
        # Here we pass Terraform variables securely into Helm via ArgoCD
        - name: env
          value: ${var.env}
        - name: kafkaBrokers
          value: ${var.bootstrap_servers}
        - name: geminiApiKey
          value: ${var.gemini_api_key}
  destination:
    server: https://kubernetes.default.svc
    namespace: default
  syncPolicy:
    automated:
      prune: true
      selfHeal: true
    syncOptions:
      - CreateNamespace=true
YAML
}
