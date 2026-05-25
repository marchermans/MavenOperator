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

---

## Phase 7 — Import & Migration (see `13-phase7-import-migration.md`)

### Storage baseline change
- [ ] `spec.storage.accessMode` defaults to `ReadWriteMany`; CEL validates enum; admission warns when StorageClass is RWO-only

### New CRD: `MavenRepositoryImport`
- [ ] CRD entity + spec/status classes; CEL: exactly one of `source.api`, `source.pvcSnapshot`, `source.pvcLive`
- [ ] `MavenRepositoryImportController` — validates target repo, resolves transfer mode, launches Job, syncs status, finalizer

### `MavenOperator.ImportJob` console app — three transfer modes

**Mode A — API crawl + direct PVC write**
- [ ] `ReposiliteApiSource`: recursive BFS via Reposilite REST API; `sinceTimestamp`; Polly retry
- [ ] `JFrogCloudApiSource`: flat-list Artifactory storage API; Bearer-token auth; group filters
- [ ] `DirectPvcSink`: write artifact bytes directly to mounted target repo PVC (no HTTP hop)
- [ ] `HttpSink` (fallback): HTTP PUT when target PVC is RWO and already claimed; operator emits `Warning`
- [ ] `PvcAccessChecker`: detect RWO conflicts before Job launch; resolve `transferMode: auto`

**Mode B — Snapshot / external PVC clone**
- [ ] `PvcSnapshotSource`: filesystem walk of mounted source PVC; `reposiliteLayout` path stripping; `mtime` filter
- [ ] Operator mounts source PVC (RO) + target PVC (RW) into Job; skips `maven-metadata.xml`
- [ ] Abort with `Failed` + `Error` condition when source PVC is RWO-bound to a running pod

**Mode C — Live Reposilite PVC clone**
- [ ] Operator stores replica count in `maven.operator.io/pre-import-replicas` annotation; scales Deployment to 0 when `scaleDownDuration > 0s`
- [ ] Finalizer `maven.operator.io/import-cleanup`: always restores Reposilite replicas on CR deletion
- [ ] Concurrent mode (`scaleDownDuration: 0s`): requires RWX PVC; `Warning` condition emitted

**Shared**
- [ ] `MavenLayoutTranslator`: strip `/<repository>/` prefix; normalise separators; remove `.index`/`.cache` dirs
- [ ] `ProgressReporter`: patch `status.artifactsCopied` on parent CR from inside Job
- [ ] `ArtifactCrawler`: bounded parallelism (`options.parallelism`); error isolation per artifact
- [ ] `ChecksumValidator`: SHA-256 post-write verification (optional)

### Performance Comparison — k6
- [ ] `k6/comparison/maven-operator.js` + `reposilite.js` — 5 scenarios (download-small, download-large, upload, metadata, mixed)
- [ ] `k6/comparison/compare.sh` — side-by-side `summary.json`
- [ ] CI `performance-comparison` job: seed via snapshot import, run compare.sh, gate on p50/p95/throughput/error-rate
- [ ] BenchmarkDotNet `ImportThroughputBenchmark`: `DirectPvcSink` ≥ 3× `HttpSink` throughput

### Tests
- [ ] Unit: `ReposiliteApiSourceTests`, `JFrogCloudApiSourceTests`, `PvcSnapshotSourceTests`, `DirectPvcSinkTests`, `HttpSinkTests`, `ArtifactCrawlerTests`, `ChecksumValidatorTests`, `MavenLayoutTranslatorTests`, `PvcAccessCheckerTests`
- [ ] Integration (Mode A): direct-write with RWX PVC; WireMock JFrog; RWO fallback to HTTP
- [ ] Integration (Mode B): Reposilite layout stripping; raw Maven layout; overwrite-skip; dry-run; RWO conflict abort
- [ ] Integration (Mode C): scale-down/restore; finalizer scale-up; concurrent RWX
- [ ] E2E: `ApiImportReposiliteE2ETest`, `SnapshotImportE2ETest`, `LivePvcImportE2ETest`, `DryRunE2ETest`, `PartialMigrationWithFiltersE2ETest`

**Done when:** All three transfer modes successfully migrate a 20-artifact Reposilite corpus
to an operator-provisioned Hosted repo; `mvn dependency:resolve` resolves every artifact;
the k6 comparison suite confirms MavenOperator meets or exceeds Reposilite performance gates;
and `DirectPvcSink` demonstrates ≥ 3× throughput vs `HttpSink` in the BenchmarkDotNet suite.

---

## Out of Scope for MVP
- LDAP authentication (deferred to Phase 8).
- `MavenRepositoryBackup` CRD.
- Web UI / dashboard.
- Artifact search / indexing.
- Quota enforcement per repo.
- Import from Nexus Repository Manager or S3.

> OIDC authentication, role-based access, and per-artifact-path ACLs are now
> **in-scope for Phase 6**. See `10-phase6-observability.md` and `04-authentication.md`.

---

## Phase 8 — Gateway API Support (see `14-gateway-api.md`)

### CRD changes
- [ ] `GatewaySpec.cs` + `GatewayRefSpec.cs` entity classes added to `Entities/Spec/`
- [ ] `spec.gateway` sub-spec added to `MavenRepositorySpec`
- [ ] CRD YAML updated in `config/crds/` and `charts/maven-operator/crds/` (in sync)
- [ ] CEL rules: mutual exclusion with `spec.ingress`, required `gatewayRef.name`

### Reconciler
- [ ] `GatewayRouteReconciler` service — idempotently creates/patches/deletes `HTTPRoute`
- [ ] Hosted / Proxy: single `PathPrefix` rule → `<name>-svc`
- [ ] Virtual: additional rule matching PUT / DELETE returning 405 (gateway-native or NGINX fallback)
- [ ] Owner reference set on `HTTPRoute`
- [ ] `status.url` derived from hostname + path (https:// when `tlsSecretRef` set)

### RBAC
- [ ] `httproutes` verb rules added to Helm `ClusterRole` in `rbac.yaml`

### Tests
- [ ] Unit: `GatewayRouteReconciler` — enabled/disabled, each repo type, missing `gatewayRef.name`
- [ ] Integration: operator creates / updates / deletes `HTTPRoute` on CRD changes
- [ ] E2E: Maven client resolves artifacts through a Gateway API HTTPRoute

**Done when:** A `MavenRepository` with `spec.gateway.enabled: true` causes the operator
to create a valid `HTTPRoute`; toggling back to `spec.gateway.enabled: false` deletes it;
and `mvn dependency:resolve` succeeds through the route in a k3d cluster with Envoy Gateway.

