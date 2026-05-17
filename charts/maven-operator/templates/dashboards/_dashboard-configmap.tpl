{{/*
Shared helper that wraps a dashboard JSON string in a labelled ConfigMap.
Usage: {{ include "maven-operator.dashboardConfigMap" (dict "name" "maven-op-01-overview" "uid" "maven-op-01" "title" "Maven Operator Overview" "json" .json "context" .) }}
*/}}
{{- define "maven-operator.dashboardConfigMap" -}}
{{- $ctx := .context -}}
{{- $ns := .context.Values.metrics.grafana.dashboards.namespace | default .context.Release.Namespace -}}
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .name }}
  namespace: {{ $ns }}
  labels:
    {{- include "maven-operator.labels" $ctx | nindent 4 }}
    {{ $ctx.Values.metrics.grafana.dashboards.label }}: {{ $ctx.Values.metrics.grafana.dashboards.labelValue | quote }}
  annotations:
    grafana-dashboard-uid: {{ .uid | quote }}
data:
  {{ .name }}.json: |-
{{ .json | indent 4 }}
{{- end -}}

