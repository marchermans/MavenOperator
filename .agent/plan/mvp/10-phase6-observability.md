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
This is sufficient for human users and small teams. Phase 6 adds:

1. **Role-based access** — predefined roles (`reader`, `deployer`, `admin`) mapped
   to download/upload permissions, reducing repetition in large deployments.
2. **CI platform OIDC trust** — accept the short-lived OIDC tokens that GitHub
   Actions and GitLab CI already mint for every job, without any pre-provisioned
   secrets. Trust is configured by declaring which repositories/projects from which
   platforms are allowed, and which role they receive.
3. **Per-artifact-path ACLs** — restrict upload or download to specific Maven
   coordinate prefixes (e.g. `com.example.*` only), evaluated after role assignment.

The key insight for CI authentication: GitHub Actions and GitLab CI **already
issue short-lived OIDC JWTs** to every job run. These tokens carry rich,
verifiable identity claims (`repository`, `ref`, `environment`, `project_path`,
`ref_protected`, etc.). There is no need to issue operator-owned tokens — instead
the auth proxy becomes a **multi-issuer OIDC trust evaluator** that maps platform
claims to local roles.

---

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

---

### B.3 CI platform OIDC trust — design

#### B.3.1 How CI platform OIDC works

Both GitHub Actions and GitLab CI expose an OIDC provider that mints a short-lived
JWT for every job run. These tokens are standard RS256-signed JWTs verifiable
against the platform's public JWKS endpoint — no secrets to pre-provision.

| Platform | OIDC Issuer | JWKS endpoint | Key identity claims |
|----------|-------------|---------------|---------------------|
| GitHub Actions | `https://token.actions.githubusercontent.com` | `https://token.actions.githubusercontent.com/.well-known/jwks` | `repository`, `repository_owner`, `ref`, `environment`, `job_workflow_ref`, `event_name` |
| GitLab.com | `https://gitlab.com` | `https://gitlab.com/oauth/discovery/keys` | `project_path`, `namespace_path`, `ref`, `ref_protected`, `environment`, `ref_type` |
| GitLab self-managed | `https://<your-gitlab-host>` | `https://<host>/oauth/discovery/keys` | same as above |

GitHub Actions example token claims:
```json
{
  "iss": "https://token.actions.githubusercontent.com",
  "sub": "repo:acme-org/my-service:environment:prod",
  "repository": "acme-org/my-service",
  "repository_owner": "acme-org",
  "workflow": "release.yml",
  "ref": "refs/heads/main",
  "ref_type": "branch",
  "environment": "prod",
  "job_workflow_ref": "acme-org/my-service/.github/workflows/release.yml@refs/heads/main",
  "event_name": "push"
}
```

GitLab CI example token claims:
```json
{
  "iss": "https://gitlab.com",
  "sub": "project_path:acme-group/my-service:ref_type:branch:ref:main",
  "project_path": "acme-group/my-service",
  "namespace_path": "acme-group",
  "ref": "main",
  "ref_type": "branch",
  "ref_protected": "true",
  "environment": "production"
}
```

#### B.3.2 Trust policy model

The `MavenRepository` CRD gains a new `auth.ciTrust` list. Each entry declares
one **trust binding**: a set of claim matchers against a specific OIDC issuer,
and the role that matching tokens receive.

```yaml
auth:
  ciTrust:
    # GitHub Actions: the release workflow on main from acme-org/my-service gets deployer
    - platform: github-actions
      role: deployer
      claims:
        repository: "acme-org/my-service"
        ref: "refs/heads/main"
        event_name: "push"

    # GitHub Actions: any workflow from the acme-org org on a protected tag gets deployer
    - platform: github-actions
      role: deployer
      claims:
        repository_owner: "acme-org"
        ref_type: "tag"

    # GitHub Actions: read-only access for all PRs from any acme-org repo
    - platform: github-actions
      role: reader
      claims:
        repository_owner: "acme-org"
        event_name: "pull_request"

    # GitLab.com: any pipeline from acme-group/my-service on a protected ref
    - platform: gitlab
      role: deployer
      claims:
        project_path: "acme-group/my-service"
        ref_protected: "true"

    # GitLab self-managed instance: group-level deployer access
    - platform: gitlab
      issuerUrl: "https://gitlab.internal.acme.com"   # overrides default gitlab.com
      role: deployer
      claims:
        namespace_path: "acme-group"
        ref: "main"
```

Claim matching rules:
- All claims listed in a binding must match (AND logic).
- String values are matched exactly (case-sensitive).
- A `*` glob wildcard is supported for prefix/suffix matching (e.g. `repository: "acme-org/*"`).
- Multiple bindings are evaluated in order; the **first match wins**.
- If no binding matches the token, authentication fails with `403 Forbidden`.

#### B.3.3 `audience` (aud) claim handling

Both platforms allow the caller to set the `aud` claim of the minted token. The
auth proxy enforces that the `aud` matches a configurable expected value to prevent
token reuse across services:

```yaml
auth:
  ciTrust:
    - platform: github-actions
      audience: "https://maven.acme.com"   # CI workflow must request this aud
      role: deployer
      claims:
        repository: "acme-org/my-service"
```

If `audience` is omitted in the binding, the auth proxy accepts any `aud` value —
acceptable for internal clusters not exposed externally.

#### B.3.4 CRD schema additions

```yaml
spec:
  auth:
    # Existing htpasswd users (unchanged)
    users:
      - secretRef: alice-credentials
        role: reader

    # New: CI platform trust bindings
    ciTrust:
      - platform: github-actions    # string enum: github-actions | gitlab
        issuerUrl: ""               # optional override; empty = platform default
        audience: ""                # optional aud claim to enforce
        role: deployer              # string enum: reader | deployer | admin
        claims:                     # map[string]string — all must match
          repository: "acme-org/my-service"
          ref: "refs/heads/main"
```

CEL validation rules on `auth.ciTrust[]`:
- `platform` must be `github-actions` or `gitlab`
- `role` must be one of `reader`, `deployer`, `admin`
- `claims` must be non-empty (a binding with no claim constraints would grant
  the role to **any** token from that issuer — rejected at admission time)
- If `platform == github-actions` and `issuerUrl` is set, the URL must use HTTPS

---

### B.4 The auth sidecar (`maven-auth-proxy`)

The same `maven-auth-proxy` ASP.NET Core sidecar introduced for OIDC in B.3 handles
both htpasswd Basic Auth fallback and CI platform JWT validation in a single process.

#### Request flow

```
Maven client                NGINX              maven-auth-proxy
     │                        │                      │
     │─── GET /repo/... ──────▶│                      │
     │                        │─── auth_request ──────▶│
     │                        │    (headers forwarded) │
     │                        │                        │── 1. Extract Authorization header
     │                        │                        │── 2. Bearer token?
     │                        │                        │      Yes → validate JWT
     │                        │                        │        a. Decode issuer claim
     │                        │                        │        b. Fetch JWKS (cached)
     │                        │                        │        c. Verify signature
     │                        │                        │        d. Evaluate ciTrust bindings
     │                        │                        │        e. Return role in X-Auth-Role
     │                        │                        │      No → Basic Auth → htpasswd check
     │                        │                        │── 3. Return 200 + X-Auth-Role
     │                        │◀── 200 X-Auth-Role ────│
     │                        │── apply role check ───▶│
     │◀── 200 artifact ────────│                      │
```

#### JWKS caching

The auth proxy caches JWKS key sets in-process keyed by issuer URL. Cache entries
expire after 1 hour (configurable). On JWT validation failure due to unknown key ID
(`kid`), the proxy immediately re-fetches JWKS (handles key rotation).

```csharp
// Pseudocode — JwksCache service
public class JwksCache : IJwksCache
{
    // Cache: issuerUrl → (JsonWebKeySet, fetchedAt)
    // TTL: 1 hour; force-refresh on unknown kid
    Task<JsonWebKeySet> GetOrFetchAsync(string issuerUrl, string kid, CancellationToken ct);
}
```

#### Trust binding evaluation

```csharp
// Pseudocode — TrustEvaluator service
public class TrustEvaluator : ITrustEvaluator
{
    // Iterate ciTrust bindings in order.
    // For each binding:
    //   1. Check platform matches issuer URL
    //   2. Check audience if specified
    //   3. Check all claim matchers (glob-aware string match)
    //   4. Return role on first match, null if no binding matches
    string? EvaluateRole(JwtSecurityToken token, IReadOnlyList<CiTrustBinding> bindings);
}
```

#### Configuration injection

The operator renders the auth proxy's configuration as a ConfigMap containing
the list of `ciTrust` bindings in JSON form, mounted into the sidecar at
`/etc/maven-auth/config.json`. The operator reconciles this ConfigMap whenever
the `MavenRepository` spec changes — the auth proxy watches the file and reloads
without restart (using `IOptionsMonitor<T>`).

---

### B.5 Using CI platform OIDC in practice

#### GitHub Actions workflow example

```yaml
# .github/workflows/deploy.yml
jobs:
  deploy:
    permissions:
      id-token: write   # required to request an OIDC token
      contents: read
    steps:
      - name: Get Maven auth token
        id: oidc
        run: |
          TOKEN=$(curl -sSL -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
            "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=https://maven.acme.com" \
            | jq -r '.value')
          echo "token=$TOKEN" >> "$GITHUB_OUTPUT"

      - name: Deploy to Maven repository
        run: |
          mvn deploy \
            -Dmaven.wagon.http.headers.Authorization="Bearer ${{ steps.oidc.outputs.token }}"
```

NGINX passes the `Authorization: Bearer <jwt>` header to `maven-auth-proxy` via
`auth_request`. The proxy validates the token, matches it against the `ciTrust`
bindings, and returns `X-Auth-Role: deployer` to NGINX.

#### GitLab CI pipeline example

```yaml
# .gitlab-ci.yml
deploy:
  id_tokens:
    MAVEN_TOKEN:
      aud: "https://maven.acme.com"
  script:
    - mvn deploy -Dmaven.wagon.http.headers.Authorization="Bearer $MAVEN_TOKEN"
```

No secrets required in either platform — only the trust binding in the
`MavenRepository` CRD.

---

### B.6 Per-artifact-path ACLs

Group-coordinate prefix restrictions added to the CRD:

```yaml
auth:
  acls:
    - path: "com/example/**"
      roles: [reader, deployer, admin]   # allowed roles for download
      uploadRoles: [deployer, admin]      # allowed roles for upload
    - path: "org/apache/**"
      roles: [reader]                     # read-only for this subtree
      uploadRoles: []                     # no uploads allowed
```

ACLs apply after role assignment (from either htpasswd or CI platform token
evaluation). NGINX `location` blocks are rendered ordered by longest-prefix-first.

---

### B.7 Authentication summary table

| Feature | Phase 4 | Phase 6 |
|---------|---------|---------|
| HTTP Basic + htpasswd | ✅ | ✅ |
| Multi-user per policy | ✅ | ✅ |
| Role-based (reader/deployer/admin) | ❌ | ✅ |
| GitHub Actions OIDC trust | ❌ | ✅ |
| GitLab CI OIDC trust | ❌ | ✅ |
| Multi-issuer / self-managed GitLab | ❌ | ✅ |
| Claim-based trust bindings (repo, ref, env…) | ❌ | ✅ |
| Audience (`aud`) enforcement | ❌ | ✅ |
| Per-artifact-path ACLs | ❌ | ✅ |
| Operator-issued tokens | ❌ | ❌ (not needed) |
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
- [ ] Extend CRD schema with `auth.users[].role` (backward-compatible with legacy `secretRefs`)
- [ ] Extend CRD schema with `auth.ciTrust[]` (platform, issuerUrl, audience, role, claims)
- [ ] CEL validation: `ciTrust[].claims` must be non-empty; `platform` and `role` must be valid enums
- [ ] `RoleBasedHtpasswdService`: filter users by role when building download/upload htpasswd files
- [ ] New project `MavenOperator.AuthProxy` — ASP.NET Core sidecar handling both Basic Auth and Bearer JWT
- [ ] `IJwksCache` service: fetch and cache JWKS per issuer URL; force-refresh on unknown `kid`
- [ ] `ITrustEvaluator` service: evaluate `ciTrust` bindings against JWT claims (glob matching, ordered, first-match)
- [ ] `IAuthProxyConfig` / `IOptionsMonitor<T>`: hot-reload from ConfigMap without sidecar restart
- [ ] Operator renders `maven-auth-proxy` ConfigMap from `ciTrust` spec and injects sidecar into NGINX pods
- [ ] NGINX `auth_request` template block rendered by `NginxConfigRenderer` (replacing direct `auth_basic` when `ciTrust` is non-empty)
- [ ] ACL location block rendering (ordered by specificity)
- [ ] Unit tests: `TrustEvaluator` — glob matching, first-match, audience enforcement, empty-claims rejection
- [ ] Unit tests: `JwksCache` — cache hit, cache miss, force-refresh on unknown kid, HTTPS-only issuer
- [ ] Unit tests: role filtering in `RoleBasedHtpasswdService`
- [ ] Unit tests: ACL `location` block ordering
- [ ] Integration tests: validate GitHub Actions JWT (use a pre-signed test JWT from the GH OIDC JWKS) → 200 deployer
- [ ] Integration tests: validate GitLab JWT (use a pre-signed test JWT) → correct role
- [ ] Integration tests: mismatched claim → 403
- [ ] Integration tests: expired JWT → 401
- [ ] Integration tests: unknown issuer → 403
- [ ] E2E tests: synthetic GitHub-format JWT → `mvn deploy` succeeds; wrong repo claim → 403
- [ ] Backward-compatibility: existing `auth.download.secretRefs` still works unmodified

---

## Related documents

- `11-nginx-metrics.md` — complete NGINX configuration additions (log format, map directives, stub_status, mtail program)
- `12-dashboards-alerts.md` — Grafana dashboard JSON/panel spec and PrometheusRule alert definitions

