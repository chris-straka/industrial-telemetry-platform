resource "kubernetes_namespace_v1" "istio_system" {
  count = var.install_istio ? 1 : 0

  metadata {
    name = "istio-system"
  }
}

resource "helm_release" "istio_base" {
  count = var.install_istio ? 1 : 0

  name       = "istio-base"
  repository = "https://istio-release.storage.googleapis.com/charts"
  chart      = "base"
  version    = var.istio_chart_version
  namespace  = kubernetes_namespace_v1.istio_system[0].metadata[0].name

  atomic          = true
  cleanup_on_fail = true
  timeout         = 600
  wait            = true
}

resource "helm_release" "istiod" {
  count = var.install_istio ? 1 : 0

  name       = "istiod"
  repository = "https://istio-release.storage.googleapis.com/charts"
  chart      = "istiod"
  version    = var.istio_chart_version
  namespace  = kubernetes_namespace_v1.istio_system[0].metadata[0].name
  depends_on = [helm_release.istio_base]

  atomic          = true
  cleanup_on_fail = true
  timeout         = 600
  wait            = true
}

resource "helm_release" "istio_ingress" {
  count = var.install_istio ? 1 : 0

  name             = "istio-ingressgateway"
  repository       = "https://istio-release.storage.googleapis.com/charts"
  chart            = "gateway"
  version          = var.istio_chart_version
  namespace        = "istio-ingress"
  create_namespace = true
  depends_on       = [helm_release.istiod]

  atomic          = true
  cleanup_on_fail = true
  timeout         = 600
  wait            = true
}
