# 09 — Milestones
## Phase 0 — Foundation (Week 1-2)
- [ ] Add KubeOps NuGet package to project.
- [ ] Define `MavenRepositoryV1Alpha1` C# entity and spec/status classes.
- [ ] Generate and apply CRD YAML to a local cluster (k3d / minikube).
- [ ] Scaffold empty `MavenRepositoryController` with KubeOps webhook for validation.
- [ ] Basic status updates (phase field).
**Done when:** `kubectl apply -f my-repo.yaml` creates the CRD, and the operator sets `status.phase = Pending`.
---
## Phase 1 — Hosted Repository (Week 3-4)
- [ ] `HostedRepositoryReconciler`: PVC, ConfigMap (NGINX config), Deployment, Service.
- [ ] `NginxConfigRenderer` with Hosted template.
- [ ] `HtpasswdService`: hash passwords, write `<name>-htpasswd` Secret.
- [ ] Auth: Anonymous download + Authenticated upload working.
- [ ] Ingress creation (optional field).
- [ ] Full reconcile on CRD update (config hash annotation triggers rolling restart).
**Done when:** `mvn deploy` against the operator-provisioned Hosted repo succeeds; `mvn dependency:resolve` also succeeds.
---
## Phase 2 — Proxy Repository (Week 5)
- [ ] `ProxyRepositoryReconciler`: ConfigMap (proxy template), Deployment, Service.
- [ ] Upstream credentials injection.
- [ ] Proxy cache (emptyDir).
**Done when:** Maven resolves `junit:junit:4.13.2` through the operator's proxy repo backed by Maven Central.
---
## Phase 3 — Virtual Repository (Week 6-7)
- [ ] `VirtualRepositoryReconciler`: C# proxy Deployment + NGINX front Deployment, Service.
- [ ] `MetadataMergeService`: parallel fetch + XML merge.
- [ ] Metadata in-memory cache.
- [ ] `405` on PUT to Virtual repo.
**Done when:** Maven resolves artifacts spread across two Hosted repos through a single Virtual repo URL.
---
## Phase 4 — Hardening & UX (Week 8-9)
- [ ] CEL validation rules in CRD schema (type-specific field requirements).
- [ ] Kubernetes Events emitted on reconcile errors.
- [ ] `status.conditions` fully populated.
- [ ] Controller watches for referenced Secret changes and re-reconciles.
- [ ] `spec.storage.deletionPolicy` (Retain vs Delete).
- [ ] Persistent proxy cache PVC option.
---
## Phase 5 — Observability & Packaging (Week 10)
- [ ] Prometheus metrics endpoint (KubeOps built-in or custom).
- [ ] Helm chart for operator deployment.
- [ ] CI: GitHub Actions — build, test, push image.
- [ ] E2E tests using `kubectl` + actual Maven clients in a k3d cluster.

---

## Phase 6 — Deep Observability & Enhanced Authentication (Week 11-13)

See `10-phase6-observability.md` for the full design.

### Part A — Deep Observability

- [ ] Extend NGINX config templates with structured JSON access log format and `map` directives for Maven coordinate extraction (see `11-nginx-metrics.md`)
- [ ] Add internal `stub_status` server block to all NGINX pod configs
- [ ] Operator injects `nginx/nginx-prometheus-exporter` sidecar into NGINX pods when `spec.metrics.enabled: true`
- [ ] Operator injects `mtail` sidecar with a per-repo `ConfigMap` containing the mtail program
- [ ] Operator creates additional named `Service` ports (`nginx-metrics:9113`, `mtail-metrics:3903`)
- [ ] Operator creates `PodMonitor` resources when prometheus-operator CRDs are detected and `metrics.podMonitor.enabled: true`
- [ ] Add `spec.metrics.*` sub-spec to CRD schema with CEL validation
- [ ] Add Helm values for sidecar images, resource limits, podMonitor toggle, Grafana toggle
- [ ] Ship 4 Grafana dashboards as Helm ConfigMaps (opt-in, see `12-dashboards-alerts.md`)
- [ ] Ship PrometheusRule with 5 recording rules + 9 alert rules (opt-in, see `12-dashboards-alerts.md`)
- [ ] Unit tests: `NginxConfigRenderer` produces correct log_format, map directives, stub_status block
- [ ] Unit tests: mtail program parses sample log lines and produces correct metric values
- [ ] Integration tests: scrape `:9113/metrics` and `:3903/metrics` — assert non-zero values after reconcile
- [ ] E2E tests: `mvn deploy` → verify `maven_artifact_requests_total{method="PUT"}` increments; `mvn dependency:resolve` → verify GET counter

**Done when:** After deploying a repo and running a Maven build, all four dashboards
render correctly in Grafana, all alerts are present in Prometheus, and artifact-level
metrics are visible per repository.

### Part B — Enhanced Authentication

- [ ] Extend CRD schema with `auth.users[].role` (reader/deployer/admin) — backward-compatible with existing `auth.*.secretRefs`
- [ ] Extend CRD schema with `auth.ciTrust[]` — multi-issuer CI platform OIDC trust bindings (platform, issuerUrl, audience, role, claims map)
- [ ] CEL admission validation: `ciTrust[].claims` must be non-empty; `platform` and `role` must be valid enums; `issuerUrl` must be HTTPS when set
- [ ] `RoleBasedHtpasswdService`: filters users by role when building download/upload htpasswd files
- [ ] New project `MavenOperator.AuthProxy` — ASP.NET Core sidecar handling both HTTP Basic Auth (htpasswd) and Bearer JWT (CI platform OIDC)
- [ ] `IJwksCache` service: per-issuer JWKS fetch and cache (1h TTL); force-refresh on unknown `kid` (key rotation)
- [ ] `ITrustEvaluator` service: evaluate `ciTrust` bindings against JWT claims — glob matching, ordered first-match, audience enforcement
- [ ] `IOptionsMonitor<AuthProxyConfig>` hot-reload from ConfigMap — no sidecar restart required on binding changes
- [ ] Operator renders auth proxy ConfigMap from `ciTrust` spec and injects `maven-auth-proxy` sidecar into NGINX pods when `ciTrust` is non-empty
- [ ] `NginxConfigRenderer`: renders `auth_request /auth/validate` block (replaces `auth_basic` when `ciTrust` is non-empty)
- [ ] ACL location block rendering (ordered by longest-prefix-first)
- [ ] Unit tests: `TrustEvaluator` — GitHub Actions claims, GitLab CI claims, glob wildcards, first-match semantics, empty-claims rejection, audience mismatch → 403
- [ ] Unit tests: `JwksCache` — cache hit path, cache miss (HTTP fetch), force-refresh on unknown kid, HTTPS-only issuer URL
- [ ] Unit tests: `RoleBasedHtpasswdService` — role filtering correctness
- [ ] Unit tests: ACL `location` block specificity ordering
- [ ] Integration tests: synthetic GitHub-format pre-signed JWT → 200 with correct role; wrong `repository` claim → 403; expired JWT → 401; unknown issuer → 403
- [ ] Integration tests: synthetic GitLab-format pre-signed JWT → 200 with correct role; `ref_protected: false` when binding requires `true` → 403
- [ ] E2E tests: GitHub-format JWT in Authorization header → `mvn deploy` succeeds end-to-end; wrong repo claim → 403 from NGINX
- [ ] Backward-compatibility: existing `auth.download.secretRefs` still works without any `ciTrust` bindings

**Done when:** A GitHub Actions workflow and a GitLab CI pipeline can each deploy
artifacts to a `MavenRepository` using only their platform-issued OIDC JWT —
no Kubernetes Secrets, no operator-issued tokens, no pre-provisioned credentials.

---

## Testing Strategy (applies to every phase)

Testing is the **primary validation metric**. A phase is not complete until its tests pass.

### Test layers

| Layer | Framework | Scope |
|-------|-----------|-------|
| Unit | xUnit + NSubstitute | Services, config renderers, metadata merger, htpasswd builder — no cluster |
| Integration | xUnit + k3d/envtest | Reconcilers against a real Kubernetes API |
| E2E | xUnit + Maven CLI (`mvn`) | Full client workflows against a running operator |
| Performance | BenchmarkDotNet + k6 | Throughput, latency, reconcile loop timing |

### Test project layout

```
MavenOperator.Tests.Unit/          # No external deps; runs on every PR
MavenOperator.Tests.Integration/   # Requires k3d; tagged [Integration]; runs on every PR in CI
MavenOperator.Tests.E2E/           # Requires full cluster + Maven; tagged [E2E]; runs on merge to main
MavenOperator.Tests.Performance/   # BenchmarkDotNet benchmarks + k6 scripts
```

### Rules
- Every service must be constructor-injected and mockable — no static state.
- Reconciler steps must be small and individually invokable so they can be unit tested in isolation.
- If something is hard to test, that is a **design smell** — fix the design, not the test.
- Performance baselines are stored in `/.benchmarks/` and gates run on every release.
- A feature with no corresponding test does **not** count as delivered.

---

## Out of Scope for MVP
- LDAP authentication (planned for Phase 7).
- `MavenRepositoryBackup` CRD.
- Web UI / dashboard.
- Artifact search / indexing.
- Quota enforcement per repo.

> OIDC authentication, role-based access, and per-artifact-path ACLs are now
> **in-scope for Phase 6**. See `10-phase6-observability.md` and `04-authentication.md`.

