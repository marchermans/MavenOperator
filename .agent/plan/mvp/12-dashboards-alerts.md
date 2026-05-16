# 12 — Grafana Dashboards & Alert Rules

## Overview

This document specifies the four Grafana dashboards and nine PrometheusRule
alert/recording rules shipped with the `maven-operator` Helm chart as optional
opt-in resources.

All dashboards are delivered as Kubernetes `ConfigMap` objects with the label
`grafana_dashboard: "1"` (configurable) so Grafana's dashboard sidecar
(`grafana/grafana-sidecar`) picks them up automatically. Alert rules are delivered
as `monitoring.coreos.com/v1` `PrometheusRule` resources.

---

## Helm opt-in

```yaml
# values.yaml
metrics:
  grafana:
    dashboards:
      enabled: true          # set to true to install dashboard ConfigMaps
      label: grafana_dashboard
      labelValue: "1"
      namespace: ""          # defaults to release namespace
    alertRules:
      enabled: true          # set to true to install PrometheusRule
      namespace: ""
      additionalLabels: {}   # e.g. release: kube-prometheus-stack
```

---

## Dashboard 1 — Maven Operator Overview (`maven-op-01`)

**Purpose**: Health of the operator control plane itself. Primary on-call
dashboard. Answers "is the operator working?".

### Panels

| Row | Panel | Visualization | Query (PromQL) |
|-----|-------|--------------|----------------|
| **Operator Health** | Reconcile rate | Time series | `sum by (repo_type) (rate(mavenoperator_reconcile_total[5m]))` |
| | Reconcile error rate | Time series (alert threshold line at 5%) | `sum by (repo_type) (rate(mavenoperator_reconcile_total{success="false"}[5m])) / sum by (repo_type) (rate(mavenoperator_reconcile_total[5m]))` |
| | p95 reconcile duration | Time series | `histogram_quantile(0.95, sum by (le, repo_type) (rate(mavenoperator_reconcile_duration_seconds_bucket[5m])))` |
| | p99 reconcile duration | Stat | `histogram_quantile(0.99, sum by (le) (rate(mavenoperator_reconcile_duration_seconds_bucket[5m])))` |
| **Repository Inventory** | Repositories by type | Pie chart | `mavenoperator_repository_count` |
| | Repositories by phase | Bar gauge | `mavenoperator_repository_count` grouped by `phase` |
| | Failed repositories | Stat (red if > 0) | `sum(mavenoperator_repository_count{phase="Failed"})` |
| **Resource Apply** | Resources applied/min | Time series | `sum by (resource_kind) (rate(mavenoperator_resource_apply_total[1m]))` |
| **Operator Pod** | CPU usage | Time series | `rate(container_cpu_usage_seconds_total{container="operator"}[5m])` |
| | Memory usage | Time series | `container_memory_working_set_bytes{container="operator"}` |

### Variables (Grafana template variables)

| Variable | Type | Query |
|----------|------|-------|
| `$namespace` | Query | `label_values(mavenoperator_repository_count, namespace)` |

### Annotations

- Operator restart events from `kube_pod_container_status_restarts_total` where `container="operator"`.

---

## Dashboard 2 — Repository Detail (`maven-op-02`)

**Purpose**: Deep dive into a single repository. Answers "is this repo healthy,
how busy is it, what's my cache hit rate, how full is storage?".

### Variables

| Variable | Type | Query |
|----------|------|-------|
| `$namespace` | Query | `label_values(maven_artifact_requests_total, namespace)` |
| `$repo` | Query | `label_values(maven_artifact_requests_total{namespace="$namespace"}, repo)` |

### Panels

| Row | Panel | Visualization | Query |
|-----|-------|--------------|-------|
| **Traffic** | Request rate | Time series | `sum by (method, status) (rate(maven_artifact_requests_total{repo="$repo"}[5m]))` |
| | Error rate (4xx + 5xx) | Time series (alert line at 1%) | `sum(rate(maven_artifact_requests_total{repo="$repo",status=~"[45].."}[5m])) / sum(rate(maven_artifact_requests_total{repo="$repo"}[5m]))` |
| | Requests by asset type | Stacked bar | `sum by (asset_type) (rate(maven:repo_requests_total{repo="$repo"}[5m]))` |
| **Latency** | p50 / p95 / p99 request duration | Time series | `histogram_quantile(0.95, sum by (le, asset_type) (rate(maven_request_duration_seconds_bucket{repo="$repo"}[5m])))` |
| | Heatmap of request duration | Heatmap | `sum by (le) (rate(maven_request_duration_seconds_bucket{repo="$repo"}[5m]))` |
| **Throughput** | Bytes served/sec | Time series | `sum by (asset_type) (rate(maven_artifact_bytes_total{repo="$repo"}[5m]))` |
| | Total bytes served today | Stat | `sum(increase(maven_artifact_bytes_total{repo="$repo"}[24h]))` |
| **Cache** (proxy repos only) | Cache hit rate | Time series + threshold at 50% | `sum(rate(maven_cache_hits_total{repo="$repo",cache_status="HIT"}[5m])) / sum(rate(maven_cache_hits_total{repo="$repo"}[5m]))` |
| | Cache status breakdown | Pie chart | `sum by (cache_status) (rate(maven_cache_hits_total{repo="$repo"}[5m]))` |
| **Storage** (hosted repos only) | PVC used % | Gauge (thresholds at 85%/95%) | `kubelet_volume_stats_used_bytes{persistentvolumeclaim=~"$repo.*"} / kubelet_volume_stats_capacity_bytes{persistentvolumeclaim=~"$repo.*"}` |
| | PVC used bytes | Time series | `kubelet_volume_stats_used_bytes{persistentvolumeclaim=~"$repo.*"}` |
| **NGINX** | Active connections | Time series | `nginx_connections_active{pod=~"$repo-nginx.*"}` |
| | Connection states | Stacked area | `nginx_connections_reading + nginx_connections_writing + nginx_connections_waiting` |

---

## Dashboard 3 — Artifact Heatmap (`maven-op-03`)

**Purpose**: Answer "which artifacts are most popular?". Useful for capacity
planning and understanding download patterns.

> ⚠️ This dashboard queries high-cardinality metrics. Apply a short time range
> (last 1h) and use recording rules where possible.

### Variables

| Variable | Type | Options |
|----------|------|---------|
| `$namespace` | Query | `label_values(maven_artifact_requests_total, namespace)` |
| `$repo` | Query | `label_values(maven_artifact_requests_total{namespace="$namespace"}, repo)` |
| `$topN` | Custom | `10, 20, 50` |
| `$asset_type` | Custom | `All, jar, pom, metadata, checksum` |

### Panels

| Panel | Visualization | Query |
|-------|--------------|-------|
| Top N artifacts by request count | Table (sortable) | `topk($topN, sum by (artifact_id, artifact_version, asset_type) (increase(maven_artifact_requests_total{repo="$repo",asset_type=~"$asset_type"}[$__range])))` |
| Top N artifacts by bytes | Table | `topk($topN, sum by (artifact_id, asset_type) (increase(maven_artifact_bytes_total{repo="$repo"}[$__range])))` |
| Request rate by artifact ID | Time series (multi-series, top 10) | `topk(10, sum by (artifact_id) (rate(maven:artifact_requests_by_id{repo="$repo"}[5m])))` |
| Asset type distribution | Donut chart | `sum by (asset_type) (increase(maven_artifact_requests_total{repo="$repo"}[$__range]))` |
| Download activity calendar | Heatmap (daily buckets) | `sum(increase(maven_artifact_requests_total{repo="$repo",method="GET"}[1h]))` |
| Unique artifact versions per day | Time series | `count(count by (artifact_id, artifact_version) (maven_artifact_requests_total{repo="$repo"}))` |

---

## Dashboard 4 — Virtual Proxy (`maven-op-04`)

**Purpose**: Health and performance of Virtual repository fan-out. Answers "are
my members healthy, is metadata merging fast?".

### Variables

| Variable | Type |
|----------|------|
| `$namespace` | Query |
| `$repo` | Query (filtered to virtual repos) |
| `$member` | Query — `label_values(virtual_proxy_member_request_duration_seconds_count{repo_name="$repo"}, member_name)` |

### Panels

| Panel | Visualization | Query |
|-------|--------------|-------|
| Overall request rate | Time series | `sum(rate(virtual_proxy_requests_total{repo_name="$repo"}[5m]))` |
| Status code breakdown | Bar gauge | `sum by (status_code) (rate(virtual_proxy_requests_total{repo_name="$repo"}[5m]))` |
| **Member health** | Member request success rate | Time series (multi-line) | `sum by (member_name) (rate(virtual_proxy_member_request_duration_seconds_count{repo_name="$repo",success="true"}[5m])) / sum by (member_name) (rate(virtual_proxy_member_request_duration_seconds_count{repo_name="$repo"}[5m]))` |
| p95 per-member latency | Time series | `histogram_quantile(0.95, sum by (le, member_name) (rate(virtual_proxy_member_request_duration_seconds_bucket{repo_name="$repo"}[5m])))` |
| Member error rate | Bar gauge (alert at 10%) | `sum by (member_name) (rate(virtual_proxy_member_request_duration_seconds_count{repo_name="$repo",success="false"}[5m]))` |
| **Metadata merge** | Metadata merge rate | Time series | `rate(virtual_proxy_metadata_merge_duration_seconds_count{repo_name="$repo"}[5m])` |
| Metadata merge p95 latency | Stat | `histogram_quantile(0.95, rate(virtual_proxy_metadata_merge_duration_seconds_bucket{repo_name="$repo"}[5m]))` |
| Members queried per merge (p50/p95) | Stat | `histogram_quantile(0.50, rate(virtual_proxy_metadata_merge_member_count_bucket{repo_name="$repo"}[5m]))` |
| **Asset breakdown** | Requests by asset type | Pie | `sum by (asset_type) (rate(virtual_proxy_requests_total{repo_name="$repo"}[5m]))` |

---

## PrometheusRule — Recording Rules

Recording rules reduce query cost for dashboards and enable alert evaluation on
pre-aggregated series.

```yaml
apiVersion: monitoring.coreos.com/v1
kind: PrometheusRule
metadata:
  name: maven-operator-recording-rules
  namespace: {{ .Release.Namespace }}
  labels:
    {{- include "maven-operator.labels" . | nindent 4 }}
    {{- with .Values.metrics.alertRules.additionalLabels }}
    {{- toYaml . | nindent 4 }}
    {{- end }}
spec:
  groups:
    - name: maven.recording
      interval: 60s
      rules:
        # Aggregate to repo + method + asset_type + status (drops artifact coords)
        - record: maven:repo_requests_total
          expr: >
            sum by (repo, method, asset_type, status)
            (maven_artifact_requests_total)

        # Aggregate to artifact_id level only (drops version — medium cardinality)
        - record: maven:artifact_requests_by_id
          expr: >
            sum by (repo, artifact_id, asset_type)
            (maven_artifact_requests_total)

        # Error rate ratio per repo (used in alerts)
        - record: maven:repo_error_rate5m
          expr: >
            sum by (repo) (rate(maven_artifact_requests_total{status=~"[45].."}[5m]))
            /
            sum by (repo) (rate(maven_artifact_requests_total[5m]))

        # Cache hit rate per repo (proxy repos)
        - record: maven:repo_cache_hit_rate30m
          expr: >
            sum by (repo) (rate(maven_cache_hits_total{cache_status="HIT"}[30m]))
            /
            sum by (repo) (rate(maven_cache_hits_total[30m]))

        # Operator reconcile error ratio per type
        - record: maven:operator_reconcile_error_rate5m
          expr: >
            sum by (repo_type)
            (rate(mavenoperator_reconcile_total{success="false"}[5m]))
            /
            sum by (repo_type) (rate(mavenoperator_reconcile_total[5m]))
```

---

## PrometheusRule — Alert Rules

```yaml
    - name: maven.alerts
      rules:
        # ── Operator alerts ──────────────────────────────────────────────────

        - alert: MavenOperatorReconcileErrors
          expr: maven:operator_reconcile_error_rate5m > 0.05
          for: 5m
          labels:
            severity: warning
          annotations:
            summary: "Maven Operator reconcile error rate is high"
            description: >
              Reconcile error rate for repo type {{ $labels.repo_type }} is
              {{ $value | humanizePercentage }} over the last 5 minutes.
              Check operator logs for details.
            runbook_url: "https://github.com/marchermans/MavenOperator/wiki/runbooks/reconcile-errors"

        - alert: MavenOperatorReconcileLatencyHigh
          expr: >
            histogram_quantile(0.95,
              sum by (le) (rate(mavenoperator_reconcile_duration_seconds_bucket[5m]))
            ) > 30
          for: 10m
          labels:
            severity: warning
          annotations:
            summary: "Maven Operator reconcile p95 latency is above 30s"
            description: >
              p95 reconcile duration is {{ $value | humanizeDuration }}.
              This may indicate cluster API server slowness or resource contention.

        - alert: MavenRepositoryNotReady
          expr: mavenoperator_repository_count{phase="Failed"} > 0
          for: 5m
          labels:
            severity: critical
          annotations:
            summary: "One or more MavenRepository resources are in Failed phase"
            description: >
              {{ $value }} repository/repositories of type {{ $labels.repo_type }}
              have been in Failed phase for more than 5 minutes.
              Run: kubectl get mavenrepository -A -o wide

        # ── Data plane alerts ────────────────────────────────────────────────

        - alert: MavenNginxDown
          expr: >
            kube_deployment_status_replicas_available{deployment=~".*-nginx"}
            /
            kube_deployment_spec_replicas{deployment=~".*-nginx"} < 1
          for: 2m
          labels:
            severity: critical
          annotations:
            summary: "NGINX deployment {{ $labels.deployment }} has no available replicas"
            description: >
              All pods for {{ $labels.deployment }} are unavailable.
              Maven clients will receive connection errors.

        - alert: MavenHighErrorRate
          expr: maven:repo_error_rate5m > 0.01
          for: 5m
          labels:
            severity: warning
          annotations:
            summary: "High HTTP error rate on Maven repository {{ $labels.repo }}"
            description: >
              Error rate for {{ $labels.repo }} is {{ $value | humanizePercentage }}.
              4xx errors may indicate auth misconfiguration; 5xx indicate NGINX/storage issues.

        # ── Storage alerts ────────────────────────────────────────────────────

        - alert: MavenStorageFull
          expr: >
            kubelet_volume_stats_used_bytes
            / kubelet_volume_stats_capacity_bytes > 0.85
          for: 10m
          labels:
            severity: warning
          annotations:
            summary: "Maven repository PVC {{ $labels.persistentvolumeclaim }} is 85% full"
            description: >
              {{ $labels.persistentvolumeclaim }} is {{ $value | humanizePercentage }} full.
              Consider expanding the PVC or cleaning up old artifacts.

        - alert: MavenStorageCritical
          expr: >
            kubelet_volume_stats_used_bytes
            / kubelet_volume_stats_capacity_bytes > 0.95
          for: 5m
          labels:
            severity: critical
          annotations:
            summary: "Maven repository PVC {{ $labels.persistentvolumeclaim }} is 95% full"
            description: >
              {{ $labels.persistentvolumeclaim }} is almost full. Maven deploys will
              fail once the volume is exhausted. Immediate action required.

        # ── Proxy cache alerts ────────────────────────────────────────────────

        - alert: MavenProxyCacheHitRateLow
          expr: maven:repo_cache_hit_rate30m < 0.5
          for: 30m
          labels:
            severity: info
          annotations:
            summary: "Maven proxy repo {{ $labels.repo }} cache hit rate is below 50%"
            description: >
              Cache hit rate for {{ $labels.repo }} is {{ $value | humanizePercentage }}
              over the last 30 minutes. This increases upstream traffic and latency.
              Consider increasing cache size or TTL.

        # ── Virtual proxy alerts ──────────────────────────────────────────────

        - alert: MavenVirtualProxyMemberUnhealthy
          expr: >
            sum by (repo_name, member_name) (
              rate(virtual_proxy_member_request_duration_seconds_count{success="false"}[5m])
            )
            /
            sum by (repo_name, member_name) (
              rate(virtual_proxy_member_request_duration_seconds_count[5m])
            ) > 0.10
          for: 5m
          labels:
            severity: warning
          annotations:
            summary: "Virtual proxy member {{ $labels.member_name }} has high error rate"
            description: >
              Member {{ $labels.member_name }} of virtual repo {{ $labels.repo_name }}
              has a {{ $value | humanizePercentage }} error rate over 5 minutes.
              Artifacts may not resolve correctly through this virtual repository.
```

---

## Dashboard JSON delivery

Dashboards are stored as Go template files in `charts/maven-operator/templates/dashboards/`.
Each file renders to a `ConfigMap` when `metrics.grafana.dashboards.enabled: true`.

```
charts/maven-operator/
  templates/
    dashboards/
      _dashboard-configmap.tpl        # shared helper: wraps JSON in ConfigMap
      maven-op-01-overview.yaml       # renders maven-op-01 dashboard JSON
      maven-op-02-repo-detail.yaml    # renders maven-op-02 dashboard JSON
      maven-op-03-artifact-heatmap.yaml
      maven-op-04-virtual-proxy.yaml
    alert-rules.yaml                  # PrometheusRule (recording + alert rules)
```

The dashboard JSON itself is maintained as raw Grafana JSON exported from a
live Grafana instance (the source-of-truth), then converted to a Helm template
by replacing hardcoded datasource UIDs with `{{ .Values.metrics.grafana.datasourceUid }}`.

### Grafana dashboard import (without Grafana sidecar)

If the Grafana sidecar is not available, dashboards can be imported manually:

```bash
# Extract dashboard JSON from the installed ConfigMap
kubectl get configmap maven-op-01-overview -n <namespace> \
  -o jsonpath='{.data.maven-op-01-overview\.json}' > /tmp/maven-op-01.json

# Import via Grafana HTTP API
curl -X POST http://grafana:3000/api/dashboards/import \
  -H "Content-Type: application/json" \
  -d "{\"dashboard\": $(cat /tmp/maven-op-01.json), \"overwrite\": true}"
```

---

## Grafana sidecar setup reference

To use the dashboard sidecar with the kube-prometheus-stack Helm chart:

```yaml
# kube-prometheus-stack values.yaml
grafana:
  sidecar:
    dashboards:
      enabled: true
      label: grafana_dashboard          # must match metrics.grafana.dashboards.label
      labelValue: "1"                   # must match metrics.grafana.dashboards.labelValue
      searchNamespace: ALL              # or restrict to specific namespace
```

Then install the maven-operator chart with:

```bash
helm upgrade maven-operator oci://ghcr.io/marchermans/charts/maven-operator \
  --set metrics.grafana.dashboards.enabled=true \
  --set metrics.grafana.alertRules.enabled=true \
  --set "metrics.grafana.alertRules.additionalLabels.release=kube-prometheus-stack"
```

---

## Alert routing recommendations

Recommended Alertmanager routing for production:

```yaml
# alertmanager.yaml snippet
route:
  group_by: [alertname, repo, namespace]
  routes:
    - match:
        severity: critical
      receiver: pagerduty-maven
      group_wait: 30s
      group_interval: 5m
      repeat_interval: 1h

    - match:
        severity: warning
      receiver: slack-maven
      group_wait: 2m
      group_interval: 10m
      repeat_interval: 4h

    - match:
        severity: info
      receiver: "null"    # suppress info; use in dashboards only
```

