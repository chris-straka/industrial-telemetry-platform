resource "helm_release" "external_secrets" {
  count = var.install_external_secrets_operator ? 1 : 0

  name             = "external-secrets"
  repository       = "https://charts.external-secrets.io"
  chart            = "external-secrets"
  version          = var.external_secrets_chart_version
  namespace        = "external-secrets"
  create_namespace = true

  atomic          = true
  cleanup_on_fail = true
  timeout         = 600
  wait            = true

  set = [
    {
      name  = "installCRDs"
      value = "true"
    }
  ]
}
