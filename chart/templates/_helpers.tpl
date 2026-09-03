{{/* Keep names release-scoped so two installs can share a namespace safely. */}}
{{- define "industrial-platform.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "industrial-platform.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := include "industrial-platform.name" . -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "industrial-platform.appName" -}}
{{- printf "%s-%s" (include "industrial-platform.fullname" .root) .name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "industrial-platform.labels" -}}
app.kubernetes.io/part-of: {{ include "industrial-platform.name" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
{{- end -}}

{{- define "industrial-platform.selectorLabels" -}}
app.kubernetes.io/name: {{ include "industrial-platform.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "industrial-platform.image" -}}
{{- $image := index .root.Values.images .name -}}
{{- required (printf "images.%s must be set" .name) $image -}}
{{- end -}}

{{- define "industrial-platform.commonEnv" -}}
- name: DOTNET_ENVIRONMENT
  value: {{ .root.Values.platform.environment | quote }}
- name: OTel__ServiceName
  value: {{ .serviceName | quote }}
- name: OTel__Endpoint
  value: {{ required "platform.otelEndpoint must identify an existing OTLP collector" .root.Values.platform.otelEndpoint | quote }}
{{- end -}}

{{- define "industrial-platform.istioPodAnnotation" -}}
{{- if .Values.istio.sidecarInjection }}
sidecar.istio.io/inject: "true"
{{- end }}
{{- end -}}
