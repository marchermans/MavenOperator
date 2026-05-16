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
- [ ] `RoleBasedHtpasswdService`: filters users by role when building download/upload htpasswd files
- [ ] Extend CRD schema with `auth.oidc` sub-spec (issuerUrl, clientId, clientSecretRef, scopes)
- [ ] New project `MavenOperator.AuthProxy` — ASP.NET Core OIDC JWT validation sidecar
- [ ] Operator injects `maven-auth-proxy` sidecar and renders `auth_request` NGINX config block when `auth.oidc.enabled: true`
- [ ] Extend CRD schema with `auth.acls[]` (path glob, allowed roles for download/upload)
- [ ] `NginxConfigRenderer` renders ACL `location` blocks ordered by longest-prefix-first
- [ ] Admission webhook validates: no duplicate usernames, OIDC fields mutually exclusive with htpasswd-only policies, ACL paths are valid globs
- [ ] Token issuance endpoint (`POST /api/v1/tokens`) — short-lived JWTs signed by operator key pair
- [ ] Unit tests: role filtering, ACL specificity ordering, JWT validation, token issuance/validation
- [ ] Integration tests: OIDC flow with local Dex instance in k3d; verify 401/403 on wrong credentials/role
- [ ] E2E tests: ACL enforcement (403 on wrong path), token-based `mvn deploy` without long-lived Secrets

**Done when:** A `MavenRepository` with `auth.oidc.enabled: true` correctly
delegates auth to Dex, enforces role-based upload/download, and a CI pipeline
can obtain a short-lived token and use it to deploy artifacts.

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

