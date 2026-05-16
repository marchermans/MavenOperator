# 11 — NGINX Metrics: Configuration & Sidecar Design

## Overview

This document covers the complete NGINX-level configuration changes required for
Phase 6 observability. It supplements `10-phase6-observability.md` with concrete
NGINX config snippets, mtail programs, and the sidecar container specifications.

---

## 1. Why two sidecars?

| Concern | Solution | Rationale |
|---------|----------|-----------|
| Connection-level aggregated metrics (active, handled, waiting…) | `nginx-prometheus-exporter` scraping `/stub_status` | Built into standard `nginx:alpine`, zero image change |
| Per-artifact request metrics (group, artifactId, version, method, status, bytes, latency) | `mtail` parsing JSON access logs | Regex-based, no Lua runtime needed, decoupled from NGINX version |

Using Lua (`nginx-lua-prometheus` / OpenResty) would require replacing
`nginx:1.27-alpine` with `openresty/openresty:alpine`, adding a custom build
step, and introducing a Lua dependency chain into the operator's image management.
The two-sidecar approach keeps the NGINX image vanilla and makes each concern
independently upgradeable and testable.

---

## 2. NGINX configuration additions

### 2.1 Stub status endpoint (internal)

Added to every server block or as a dedicated internal server:

```nginx
# Internal stub_status — NOT exposed on the public port.
server {
    listen 9080;          # internal only; not in the Service selector
    server_name localhost;
    allow 127.0.0.1;
    deny all;

    location /stub_status {
        stub_status;
    }
}
```

The `nginx-prometheus-exporter` sidecar is configured with
`--nginx.scrape-uri=http://127.0.0.1:9080/stub_status`.

### 2.2 Maven artifact variable extraction via map directives

NGINX `map` blocks decompose the request URI into Maven coordinate components.
These are used both in the access log and (optionally) in LRU-capped log sampling.

```nginx
# Place in the http { } context, before any server { } blocks.

# Extract the repository name from the URI prefix.
# URI pattern: /repository/<repo-name>/<group>/<artifactId>/<version>/<file>
map $uri $maven_repo {
    ~^/repository/(?P<repo>[^/]+)/  $repo;
    default                          "-";
}

# Extract Maven group ID (the path segment after the repo name, with / replaced by .).
map $uri $maven_artifact_group {
    ~^/repository/[^/]+/(?P<g>(?:[^/]+/)+)[^/]+/[^/]+/[^/]+$
        $g;                                    # raw path form, e.g. "com/example/"
    default "-";
}

# Extract artifact ID.
map $uri $maven_artifact_id {
    ~^/repository/[^/]+/(?:[^/]+/)+(?P<a>[^/]+)/[^/]+/[^/]+$
        $a;
    default "-";
}

# Extract version.
map $uri $maven_artifact_version {
    ~^/repository/[^/]+/(?:[^/]+/)+[^/]+/(?P<v>[^/]+)/[^/]+$
        $v;
    default "-";
}

# Classify asset type from file extension.
map $uri $maven_asset_type {
    ~\.jar$                 jar;
    ~\.war$                 war;
    ~\.aar$                 aar;
    ~\.pom$                 pom;
    ~maven-metadata\.xml$   metadata;
    ~\.(sha1|md5|sha256)$   checksum;
    default                 other;
}
```

### 2.3 Structured JSON access log format

```nginx
# http { } context

log_format maven_json escape=json
    '{'
    '"time":"$time_iso8601",'
    '"repo":"$maven_repo",'
    '"method":"$request_method",'
    '"path":"$request_uri",'
    '"artifact_group":"$maven_artifact_group",'
    '"artifact_id":"$maven_artifact_id",'
    '"artifact_version":"$maven_artifact_version",'
    '"asset_type":"$maven_asset_type",'
    '"status":$status,'
    '"bytes_sent":$bytes_sent,'
    '"request_time":$request_time,'
    '"cache_status":"$upstream_cache_status",'
    '"upstream_rt":"$upstream_response_time",'
    '"remote_addr":"$remote_addr"'
    '}';

# Each server block uses this format, writing to a shared log file
# that the mtail sidecar tails.
access_log /var/log/nginx/access.json maven_json;
```

The log file is written to a shared `emptyDir` volume mounted at
`/var/log/nginx/` that both the NGINX container and the `mtail` sidecar mount.

### 2.4 Cache status variable (proxy repos only)

For proxy repos, `$upstream_cache_status` is already populated by NGINX with
values: `HIT`, `MISS`, `EXPIRED`, `BYPASS`, `STALE`, `UPDATING`, `REVALIDATED`.
No additional config is needed — the variable is included in the log format above.

---

## 3. mtail program

The mtail program lives in a ConfigMap named `<repo-name>-mtail-config` and is
mounted into the mtail sidecar at `/etc/mtail/maven.mtail`.

```mtail
# maven.mtail — parse structured JSON Maven access logs
# Emits Prometheus metrics for scraping by Prometheus.

# ── Counters ──────────────────────────────────────────────────────────────────

counter maven_artifact_requests_total by repo, method, artifact_group, artifact_id, artifact_version, asset_type, status
counter maven_artifact_bytes_total    by repo, artifact_group, artifact_id, asset_type
counter maven_cache_hits_total        by repo, cache_status
counter maven_upload_bytes_total      by repo, artifact_group, artifact_id

# ── Histograms ────────────────────────────────────────────────────────────────
# mtail uses buckets for histograms.

histogram maven_request_duration_seconds by repo, method, asset_type, status buckets 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10

# ── Parsing rule ──────────────────────────────────────────────────────────────

/^{.*}$/ {
    # mtail can parse JSON using the json() decoder (mtail v3.0.6+)
    json() {
        # Pull fields from the decoded JSON object.
        /time/             { }
        /repo/             { $_repo = $0 }
        /method/           { $_method = $0 }
        /artifact_group/   { $_group = $0 }
        /artifact_id/      { $_aid = $0 }
        /artifact_version/ { $_version = $0 }
        /asset_type/       { $_atype = $0 }
        /status/           { $_status = $0 }
        /bytes_sent/       { $_bytes = $0 }
        /request_time/     { $_rt = $0 }
        /cache_status/     { $_cache = $0 }
    }

    # Increment request counter.
    maven_artifact_requests_total[$_repo][$_method][$_group][$_aid][$_version][$_atype][$_status]++

    # Accumulate bytes served.
    maven_artifact_bytes_total[$_repo][$_group][$_aid][$_atype] += $_bytes

    # Record request duration.
    maven_request_duration_seconds[$_repo][$_method][$_atype][$_status] = $_rt

    # Cache hits (populated only for proxy repos; "-" for hosted).
    $_cache != "-" {
        maven_cache_hits_total[$_repo][$_cache]++
    }

    # Upload bytes (PUT/DELETE methods).
    $_method == "PUT" {
        maven_upload_bytes_total[$_repo][$_group][$_aid] += $_bytes
    }
}
```

> **mtail version requirement**: `v3.0.6+` for the `json()` decoder. The Helm
> chart pins the image to `gcr.io/google-containers/mtail:v3.0.8`.

---

## 4. Pod template additions (sidecar spec)

The operator injects these two containers into every NGINX pod when
`spec.metrics.enabled: true`:

```yaml
# nginx-prometheus-exporter sidecar
- name: nginx-exporter
  image: nginx/nginx-prometheus-exporter:1.4
  args:
    - --nginx.scrape-uri=http://127.0.0.1:9080/stub_status
  ports:
    - name: nginx-metrics
      containerPort: 9113
      protocol: TCP
  resources:
    limits:   { cpu: 50m,  memory: 32Mi }
    requests: { cpu: 10m,  memory: 16Mi }
  securityContext:
    allowPrivilegeEscalation: false
    readOnlyRootFilesystem: true
    capabilities: { drop: [ALL] }

# mtail sidecar
- name: mtail
  image: gcr.io/google-containers/mtail:v3.0.8
  args:
    - --progs=/etc/mtail
    - --logs=/var/log/nginx/access.json
    - --port=3903
  ports:
    - name: mtail-metrics
      containerPort: 3903
      protocol: TCP
  volumeMounts:
    - name: nginx-logs
      mountPath: /var/log/nginx
      readOnly: true
    - name: mtail-config
      mountPath: /etc/mtail
      readOnly: true
  resources:
    limits:   { cpu: 100m, memory: 64Mi }
    requests: { cpu: 20m,  memory: 32Mi }
  securityContext:
    allowPrivilegeEscalation: false
    readOnlyRootFilesystem: true
    capabilities: { drop: [ALL] }
```

Volumes added to the pod:

```yaml
volumes:
  # Shared log volume between nginx and mtail
  - name: nginx-logs
    emptyDir: {}
  # mtail program ConfigMap (rendered by operator)
  - name: mtail-config
    configMap:
      name: <repo-name>-mtail-config
```

The NGINX container also gains a `volumeMount` for `nginx-logs`:

```yaml
volumeMounts:
  - name: nginx-logs
    mountPath: /var/log/nginx
```

---

## 5. Service port additions

The operator adds two named ports to the per-repo `Service`:

```yaml
ports:
  - name: http
    port: 80
    targetPort: 80
  - name: nginx-metrics       # nginx-prometheus-exporter
    port: 9113
    targetPort: 9113
  - name: mtail-metrics       # mtail
    port: 3903
    targetPort: 3903
```

---

## 6. PodMonitor (when prometheus-operator is present)

```yaml
apiVersion: monitoring.coreos.com/v1
kind: PodMonitor
metadata:
  name: <repo-name>-metrics
  namespace: <repo-namespace>
  labels:
    app: <repo-name>
    maven.operator.io/repo: <repo-name>
spec:
  selector:
    matchLabels:
      app: <repo-name>-nginx
  podMetricsEndpoints:
    - port: nginx-metrics
      interval: 30s
      scrapeTimeout: 10s
      path: /metrics
    - port: mtail-metrics
      interval: 30s
      scrapeTimeout: 10s
      path: /metrics
```

The operator creates this `PodMonitor` only when:
1. `spec.metrics.enabled: true` in the `MavenRepository` CR, AND
2. The `monitoring.coreos.com/v1` CRD group is discoverable (operator checks at startup)

---

## 7. Cardinality management

High label cardinality (`artifact_group × artifact_id × artifact_version`) can
produce millions of time series in large repos. Three mitigations:

### 7.1 mtail LRU eviction

The operator configures mtail with `--metric_expiry_interval` to expire metrics
for artifact versions not seen recently. Default: 7 days.

```yaml
args:
  - --metric_expiry_interval=168h  # 7 days
```

### 7.2 Recording rules for aggregation

The PrometheusRule shipped with the Helm chart includes **recording rules** that
pre-aggregate the high-cardinality metrics into useful lower-cardinality summaries:

```yaml
# Aggregate to repo + asset_type only (drops artifact coordinates)
- record: maven:repo_requests_total
  expr: sum by (repo, method, asset_type, status) (maven_artifact_requests_total)

# Top artifact IDs by request count (kept at artifact_id level only)
- record: maven:artifact_requests_by_id
  expr: sum by (repo, artifact_id, asset_type) (maven_artifact_requests_total)
```

Grafana dashboards query these recording rules by default for overview panels,
and query the raw high-cardinality metric only in the drill-down "artifact heatmap"
dashboard (with a time range selector to limit the query window).

### 7.3 `spec.metrics.maxLabelCardinality`

When set, the `NginxConfigRenderer` adds an mtail flag
`--max_label_size=<N>` (requires custom mtail build — tracked as a future option)
OR the operator applies a Prometheus `namespaceSelector`-scoped `LimitRange` on
the recording rules. This is a best-effort guard, not a hard limit in v1.

---

## 8. Testing strategy

| Test type | What to test |
|-----------|-------------|
| Unit — `NginxConfigRenderer` | Map directives are rendered with correct regex; log_format block present; stub_status server block present |
| Unit — mtail program | Parse 10 sample log lines (GET jar, PUT pom, proxy HIT, proxy MISS, 404, 500) and assert counter values |
| Integration | After reconcile, scrape `:9113/metrics` and `:3903/metrics` from the test cluster; assert non-zero values |
| E2E | `mvn deploy` a jar, then assert `maven_artifact_requests_total{method="PUT",asset_type="jar"}` increments |
| E2E | `mvn dependency:resolve` and assert `maven_artifact_requests_total{method="GET",status="200"}` > 0 |

