# 10 — Phase 6: Deep Observability & Enhanced Authentication

## Goals

Phase 6 extends the operator's production-readiness in two orthogonal dimensions:

1. **Deep Observability** — per-artifact, per-repository metrics from every NGINX
   instance, surfaced as Prometheus metrics, with a bundled Grafana dashboard and
   alert rules that users can opt into.

2. **Enhanced Authentication** — role-based access within repositories, OIDC/LDAP
   delegation, and per-artifact-path ACLs, graduating the auth model from "username/
   password htpasswd" to a proper identity-aware system.

---

## Part A — Deep Observability

### A.1 Metrics collection strategy

The operator currently exports its own reconciler metrics and the virtual proxy
exports fan-out metrics (see Phase 5). What is still missing is **per-artifact
metrics from the NGINX data plane** itself:

| Question | Source needed |
|----------|---------------|
| How many times was `com.example:my-lib:1.2.3` downloaded? | NGINX access logs |
| What is the p95 download latency for `.jar` files? | NGINX access logs |
| How many bytes were served by `releases` today? | NGINX access logs |
| Is the proxy cache hit rate healthy? | NGINX stub_status + cache vars |
| Are connections approaching limits? | NGINX stub_status |

Two complementary mechanisms cover all of these:

#### A.1.1 NGINX stub_status + nginx-prometheus-exporter sidecar

`nginx:1.27-alpine` (the image used today) includes `ngx_http_stub_status_module`
in its standard build. This gives connection-level aggregated metrics.

A lightweight **`nginx/nginx-prometheus-exporter`** sidecar container is injected
into each NGINX pod by the operator. The sidecar scrapes `http://localhost:8080/stub_status`
and exposes the result as Prometheus metrics on port `9113`.

Metrics exposed:

| Metric | Description |
|--------|-------------|
| `nginx_connections_active` | Currently active connections |
| `nginx_connections_accepted_total` | Total accepted connections |
| `nginx_connections_handled_total` | Total handled connections |
| `nginx_connections_reading` | Connections reading request |
| `nginx_connections_waiting` | Idle keep-alive connections |
| `nginx_connections_writing` | Connections writing response |
| `nginx_http_requests_total` | Total HTTP requests |

This approach requires **no image change**, no custom build, no Lua runtime.

#### A.1.2 Structured JSON access log → mtail sidecar (per-artifact metrics)

NGINX is configured to write access logs in a **structured JSON format** that
includes every field needed for per-artifact metrics:

```nginx
log_format maven_json escape=json
  '{"time":"$time_iso8601",'
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
  '"upstream_response_time":"$upstream_response_time",'
  '"remote_addr":"$remote_addr"}';
```

The `$maven_*` variables are extracted using NGINX `map` directives that parse
`$request_uri` with regex captures. See `11-nginx-metrics.md` for the full NGINX
config additions.

A **second sidecar** (`google/mtail:v3`) tails the JSON log file and parses it
into Prometheus metrics, exposing them on port `3903`.

Metrics exposed by mtail:

| Metric | Labels | Type |
|--------|--------|------|
| `maven_artifact_requests_total` | `repo`, `method`, `artifact_group`, `artifact_id`, `artifact_version`, `asset_type`, `status` | Counter |
| `maven_artifact_bytes_total` | `repo`, `artifact_group`, `artifact_id`, `asset_type` | Counter |
| `maven_request_duration_seconds` | `repo`, `method`, `asset_type`, `status` | Histogram |
| `maven_cache_hits_total` | `repo`, `cache_status` | Counter (proxy repos only) |
| `maven_upload_bytes_total` | `repo`, `artifact_group`, `artifact_id` | Counter (hosted repos only) |

> **Cardinality note**: `artifact_group + artifact_id + artifact_version` as labels
> can produce very high cardinality in busy repos. This is **intentional** — it is
> the primary ask of Phase 6. Users can configure a `metrics.maxLabelCardinality`
> field in the CRD to cap the number of distinct label combinations mtail tracks
> (older combinations are evicted LRU). Default: 10 000.

#### A.1.3 Virtual Proxy metrics (already in Phase 5 — extended here)

The C# VirtualProxy already exposes `virtual_proxy_requests_total` with
`asset_path` and `asset_type` labels (Phase 5). In Phase 6 this is extended to
also emit per-member cache hit statistics and upstream error rates, aligning the
virtual proxy metrics schema with the NGINX-level schema.

---

### A.2 Sidecar injection — operator changes

The operator's `HostedRepositoryReconciler`, `ProxyRepositoryReconciler` (and
optionally virtual) will include the sidecar containers in the pod template when
`spec.metrics.enabled: true` (default `true`).

```yaml
# MavenRepository CRD — new metrics sub-spec
spec:
  metrics:
    enabled: true                # inject sidecars, default: true
    maxLabelCardinality: 10000   # mtail LRU limit for high-cardinality labels
    stubStatus:
      port: 8080                 # internal stub_status port (not exposed externally)
    exporterPort: 9113           # nginx-prometheus-exporter scrape port
    mtail:
      port: 3903                 # mtail metrics port
```

The operator generates a single `Service` per repo that exposes both sidecar
ports (9113 and 3903) under named ports `nginx-metrics` and `mtail-metrics`.

If `prometheus-operator` is installed and `metrics.serviceMonitor.enabled: true`
(from the Helm values), the operator also creates a `PodMonitor` (not a
`ServiceMonitor`) so Prometheus scrapes all NGINX pod replicas independently.

---

### A.3 CRD changes

```yaml
spec:
  metrics:
    enabled: true                # bool, default true
    maxLabelCardinality: 10000   # int, default 10000
    stubStatus:
      port: 8080                 # int, default 8080
    exporterPort: 9113           # int, default 9113
    mtailPort: 3903              # int, default 3903
```

Admission webhook validates:
- `exporterPort != mtailPort`
- Both ports must be in range 1024–65535

---

### A.4 Helm chart changes

New Helm values added under `metrics`:

```yaml
metrics:
  # Enable sidecar injection into NGINX pods
  sidecars:
    enabled: true
    nginxExporter:
      image: nginx/nginx-prometheus-exporter:1.4
      resources:
        limits: { cpu: 50m, memory: 32Mi }
        requests: { cpu: 10m, memory: 16Mi }
    mtail:
      image: gcr.io/google-containers/mtail:v3.0.8
      resources:
        limits: { cpu: 100m, memory: 64Mi }
        requests: { cpu: 20m, memory: 32Mi }

  # PodMonitor for per-pod scraping
  podMonitor:
    enabled: false          # requires prometheus-operator CRDs
    additionalLabels: {}
    interval: 30s
    scrapeTimeout: 10s

  # Grafana dashboards (ConfigMaps labelled for grafana-sidecar)
  grafana:
    dashboards:
      enabled: false        # requires Grafana with dashboard sidecar
      namespace: ""         # defaults to release namespace
      label: grafana_dashboard
      labelValue: "1"
    alertRules:
      enabled: false        # requires prometheus-operator CRDs
      namespace: ""
      additionalLabels: {}
```

---

### A.5 Grafana dashboards

See `12-dashboards-alerts.md` for the full dashboard specification.

Summary of dashboards shipped:

| Dashboard | UID | Description |
|-----------|-----|-------------|
| `maven-operator-overview` | `maven-op-01` | Operator health — reconcile rate, errors, resource counts |
| `maven-repository-detail` | `maven-op-02` | Per-repository drill-down — requests, latency, cache stats, storage |
| `maven-artifact-heatmap` | `maven-op-03` | Top-N artifacts by requests/bytes, asset type breakdown |
| `maven-virtual-proxy` | `maven-op-04` | Virtual repo fan-out latency, member health, metadata merge cost |

Dashboards are shipped as Helm-rendered ConfigMaps with the label
`grafana_dashboard: "1"` (overridable via `metrics.grafana.dashboards.label`).
Grafana's [dashboard sidecar](https://github.com/grafana/helm-charts/blob/main/charts/grafana/README.md#sidecar-for-dashboards)
picks these up automatically with no manual import.

---

### A.6 Alert rules

See `12-dashboards-alerts.md` for the full alert specification.

Summary of PrometheusRule alerts shipped:

| Alert | Severity | Condition |
|-------|----------|-----------|
| `MavenOperatorReconcileErrors` | warning | Reconcile error rate > 5% over 5 min |
| `MavenOperatorReconcileLatencyHigh` | warning | p95 reconcile loop > 30s |
| `MavenRepositoryNotReady` | critical | Any repo in `Failed` phase for > 5 min |
| `MavenNginxDown` | critical | NGINX pod not ready for > 2 min |
| `MavenStorageFull` | warning | PVC used > 85% |
| `MavenStorageCritical` | critical | PVC used > 95% |
| `MavenHighErrorRate` | warning | HTTP 5xx rate > 1% over 5 min |
| `MavenProxyCacheHitRateLow` | info | Cache hit rate < 50% over 30 min |
| `MavenVirtualProxyMemberUnhealthy` | warning | Any member error rate > 10% |

---

## Part B — Enhanced Authentication

### B.1 Motivation

Phase 4 delivered multi-user htpasswd with separate download/upload policies.
This is sufficient for small teams. Phase 6 adds:

1. **Role-based access** — predefined roles (`reader`, `deployer`, `admin`) mapped
   to download/upload permissions, reducing repetition in large deployments.
2. **OIDC delegation** — delegate authentication to an external OIDC provider
   (Dex, Keycloak, GitHub OIDC) using NGINX's `auth_request` sub-request.
3. **Per-artifact-path ACLs** — restrict upload or download to specific Maven group
   coordinate prefixes (e.g. `com.example.*` only).
4. **Token-based auth** — short-lived bearer tokens issued by the operator, reducing
   secret rotation burden for CI pipelines.

### B.2 Role-based access

Three predefined roles, configurable per-user:

| Role | Download | Upload | Admin API |
|------|----------|--------|-----------|
| `reader` | ✅ | ❌ | ❌ |
| `deployer` | ✅ | ✅ | ❌ |
| `admin` | ✅ | ✅ | ✅ |

The CRD `secretRefs` list is extended with an optional `role` field:

```yaml
auth:
  download:
    policy: Authenticated
  upload:
    policy: Authenticated
  users:
    - secretRef: alice-credentials
      role: reader
    - secretRef: ci-bot-credentials
      role: deployer
    - secretRef: admin-credentials
      role: admin
```

The operator generates **two** htpasswd files as before (download and upload),
but now filters by role: only `deployer` and `admin` are included in the upload
htpasswd.

> **Backward compatibility**: the existing `auth.download.secretRefs` /
> `auth.upload.secretRefs` lists continue to work unchanged (all users get the
> implicit `deployer` role when referenced via the legacy path).

### B.3 OIDC delegation via auth_request

When `auth.oidc.enabled: true`, NGINX is configured to use `auth_request` to
delegate every request to a lightweight **auth sidecar** (the operator deploys
a small Go/C# sidecar — `maven-auth-proxy`) that validates OIDC JWTs.

```yaml
auth:
  oidc:
    enabled: true
    issuerUrl: https://accounts.google.com
    clientId: maven-operator
    clientSecretRef: oidc-client-secret
    downloadScopes: ["openid", "maven:read"]
    uploadScopes:   ["openid", "maven:write"]
```

NGINX config pattern:

```nginx
location /repository/my-releases/ {
    auth_request /auth/validate;
    auth_request_set $auth_user $upstream_http_x_auth_user;
    auth_request_set $auth_role $upstream_http_x_auth_role;

    limit_except GET HEAD OPTIONS {
        # Upload requires deployer or admin role (returned in header by auth sidecar)
        set $upload_allowed 0;
        if ($auth_role = "deployer") { set $upload_allowed 1; }
        if ($auth_role = "admin")    { set $upload_allowed 1; }
        if ($upload_allowed = 0)     { return 403; }
    }
    # ... WebDAV directives
}

location /auth/validate {
    internal;
    proxy_pass http://localhost:9200/validate;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_set_header X-Original-URI $request_uri;
    proxy_set_header X-Original-Method $request_method;
}
```

The auth sidecar (`maven-auth-proxy`) is a minimal ASP.NET Core service that:
- Validates the `Authorization: Bearer <jwt>` header against the OIDC provider's JWKS endpoint
- Returns `200` with `X-Auth-User` and `X-Auth-Role` headers on success
- Returns `401` on missing/invalid token
- Caches validated tokens in-process for the JWT's remaining TTL (reducing OIDC round-trips)

### B.4 Per-artifact-path ACLs

Group-coordinate prefix restrictions added to the CRD:

```yaml
auth:
  acls:
    - path: "com/example/**"
      roles: [reader, deployer, admin]   # allowed roles for download
      uploadRoles: [deployer, admin]      # allowed roles for upload
    - path: "org/apache/**"
      roles: [reader]                     # read-only for this subtree
      uploadRoles: []                     # no uploads
```

These are translated into NGINX `location` blocks ordered by specificity. The
operator renders them at reconcile time using a Scriban template. Overlapping
paths resolve by longest-prefix wins (NGINX `location` precedence).

### B.5 Token-based auth

The operator exposes a new sub-resource API endpoint (as a Kubernetes aggregated
API or a simple HTTP service):

```
POST /api/v1/tokens
{
  "repository": "releases",
  "namespace": "my-repos",
  "role": "deployer",
  "ttl": "24h"
}
→ { "token": "mvn-tok-abc123...", "expiresAt": "2026-05-17T10:00:00Z" }
```

Tokens are short-lived JWTs signed by a per-operator key pair (stored in a
Kubernetes Secret). The auth sidecar validates these tokens using the operator's
public key, avoiding OIDC round-trips.

CI pipelines request a fresh token at the start of each pipeline run and use it
as the Maven/Gradle password — no long-lived password Secrets needed.

### B.6 Authentication summary table

| Feature | Phase 4 | Phase 6 |
|---------|---------|---------|
| HTTP Basic + htpasswd | ✅ | ✅ |
| Multi-user per policy | ✅ | ✅ |
| Role-based (reader/deployer/admin) | ❌ | ✅ |
| OIDC / JWT delegation | ❌ | ✅ |
| Per-artifact-path ACLs | ❌ | ✅ |
| Short-lived token issuance | ❌ | ✅ |
| LDAP delegation | ❌ | 🔜 Phase 7 |

---

## Deliverables checklist

### Observability
- [ ] Switch NGINX config templates to emit structured JSON access logs
- [ ] Add `nginx/nginx-prometheus-exporter` sidecar injection to all NGINX pod templates
- [ ] Add `mtail` sidecar injection with an mtail program for Maven access log parsing
- [ ] Operator creates `PodMonitor` resources when `metrics.podMonitor.enabled: true`
- [ ] Add `spec.metrics.*` fields to CRD schema with CEL validation
- [ ] Unit tests: `NginxConfigRenderer` log_format output
- [ ] Unit tests: mtail program parsing (input log → expected metrics)
- [ ] Integration tests: scrape both sidecar metric endpoints after operator creates a repo
- [ ] E2E tests: deploy a repo, perform GET/PUT, verify metrics increment correctly
- [ ] Grafana dashboards as Helm ConfigMaps (4 dashboards — see `12-dashboards-alerts.md`)
- [ ] PrometheusRule alert rules as Helm resource (9 alerts — see `12-dashboards-alerts.md`)
- [ ] Helm values for sidecar images, resources, podMonitor, grafana toggle
- [ ] README: metrics section updated

### Authentication
- [ ] Extend CRD schema with `auth.users[].role`, `auth.oidc`, `auth.acls`
- [ ] `RoleBasedHtpasswdService`: filter users by role when building htpasswd files
- [ ] `OidcAuthSidecar`: new ASP.NET Core project `MavenOperator.AuthProxy`
- [ ] Operator injects `maven-auth-proxy` sidecar when `auth.oidc.enabled: true`
- [ ] NGINX `auth_request` template block rendered by `NginxConfigRenderer`
- [ ] ACL location block rendering (ordered by specificity)
- [ ] Token issuance endpoint (operator HTTP API or aggregated API)
- [ ] Unit tests: role filtering, ACL ordering, JWT validation, token issuance
- [ ] Integration tests: OIDC flow with a local Dex instance in k3d
- [ ] E2E tests: verify 403 on wrong role, 401 on missing token, ACL enforcement
- [ ] Backward-compatibility: existing `auth.download.secretRefs` still works

---

## Related documents

- `11-nginx-metrics.md` — complete NGINX configuration additions (log format, map directives, stub_status, mtail program)
- `12-dashboards-alerts.md` — Grafana dashboard JSON/panel spec and PrometheusRule alert definitions

