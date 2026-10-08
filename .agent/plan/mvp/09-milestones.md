# 09 — Milestones
## Phase 0 — Foundation (Week 1-2)
- [x] Add KubeOps NuGet package to project.
- [x] Define `MavenRepositoryV1Alpha1` C# entity and spec/status classes.
- [x] Generate and apply CRD YAML to a local cluster (k3d / minikube).
- [x] Scaffold empty `MavenRepositoryController` with KubeOps webhook for validation.
- [x] Basic status updates (phase field).
**Done when:** `kubectl apply -f my-repo.yaml` creates the CRD, and the operator sets `status.phase = Pending`.
---
## Phase 1 — Hosted Repository (Week 3-4)
- [x] `HostedRepositoryReconciler`: PVC, ConfigMap (NGINX config), Deployment, Service.
- [x] `NginxConfigRenderer` with Hosted template.
- [x] `HtpasswdService`: hash passwords, write `<name>-htpasswd` Secret.
- [x] Auth: Anonymous download + Authenticated upload working.
- [x] Ingress creation (optional field).
- [x] Full reconcile on CRD update (config hash annotation triggers rolling restart).
**Done when:** `mvn deploy` against the operator-provisioned Hosted repo succeeds; `mvn dependency:resolve` also succeeds.
---
## Phase 2 — Proxy Repository (Week 5)
- [x] `ProxyRepositoryReconciler`: ConfigMap (proxy template), Deployment, Service.
- [x] Upstream credentials injection.
- [x] Proxy cache (emptyDir).
**Done when:** Maven resolves `junit:junit:4.13.2` through the operator's proxy repo backed by Maven Central.
---
## Phase 3 — Virtual Repository (Week 6-7)
- [x] `VirtualRepositoryReconciler`: C# proxy Deployment + NGINX front Deployment, Service.
- [x] `MetadataMergeService`: parallel fetch + XML merge.
- [x] Metadata in-memory cache.
- [x] `405` on PUT to Virtual repo.
**Done when:** Maven resolves artifacts spread across two Hosted repos through a single Virtual repo URL.
---
## Phase 4 — Hardening & UX (Week 8-9)
- [x] CEL validation rules in CRD schema (type-specific field requirements).
- [x] Kubernetes Events emitted on reconcile errors.
- [x] `status.conditions` fully populated.
- [x] Controller watches for referenced Secret changes and re-reconciles.
- [x] `spec.storage.deletionPolicy` (Retain vs Delete).
- [x] Persistent proxy cache PVC option.
---
## Phase 5 — Observability & Packaging (Week 10)
- [x] Prometheus metrics endpoint (KubeOps built-in or custom).
- [x] Helm chart for operator deployment.
- [x] CI: GitHub Actions — build, test, push image.
- [x] E2E tests using `kubectl` + actual Maven clients in a k3d cluster.

---

## Phase 6 — Deep Observability & Enhanced Authentication (Week 11-13)

See `10-phase6-observability.md` for the full design.

### Part A — Deep Observability

- [x] Extend NGINX config templates with structured JSON access log format and `map` directives for Maven coordinate extraction (see `11-nginx-metrics.md`)
- [x] Add internal `stub_status` server block to all NGINX pod configs
- [x] Operator injects `nginx/nginx-prometheus-exporter` sidecar into NGINX pods when `spec.metrics.enabled: true`
- [x] Operator injects `mtail` sidecar with a per-repo `ConfigMap` containing the mtail program
- [x] Operator creates additional named `Service` ports (`nginx-metrics:9113`, `mtail-metrics:3903`)
- [x] Operator creates `PodMonitor` resources when prometheus-operator CRDs are detected and `metrics.podMonitor.enabled: true`
- [x] Add `spec.metrics.*` sub-spec to CRD schema with CEL validation
- [x] Add Helm values for sidecar images, resource limits, podMonitor toggle, Grafana toggle
- [x] Ship 4 Grafana dashboards as Helm ConfigMaps (opt-in, see `12-dashboards-alerts.md`)
- [x] Ship PrometheusRule with 5 recording rules + 9 alert rules (opt-in, see `12-dashboards-alerts.md`)
- [x] Unit tests: `NginxConfigRenderer` produces correct log_format, map directives, stub_status block
- [x] Unit tests: mtail program parses sample log lines and produces correct metric values
- [x] Integration tests: scrape `:9113/metrics` and `:3903/metrics` — assert non-zero values after reconcile
- [x] E2E tests: `mvn deploy` → verify `maven_artifact_requests_total{method="PUT"}` increments; `mvn dependency:resolve` → verify GET counter

**Done when:** After deploying a repo and running a Maven build, all four dashboards
render correctly in Grafana, all alerts are present in Prometheus, and artifact-level
metrics are visible per repository.

### Part B — Enhanced Authentication

- [x] Extend CRD schema with `auth.users[].role` (reader/deployer/admin) — backward-compatible with existing `auth.*.secretRefs`
- [x] Extend CRD schema with `auth.ciTrust[]` — multi-issuer CI platform OIDC trust bindings (platform, issuerUrl, audience, role, claims map)
- [x] CEL admission validation: `ciTrust[].claims` must be non-empty; `platform` and `role` must be valid enums; `issuerUrl` must be HTTPS when set
- [x] `RoleBasedHtpasswdService`: filters users by role when building download/upload htpasswd files
- [x] New project `MavenOperator.AuthProxy` — ASP.NET Core sidecar handling both HTTP Basic Auth (htpasswd) and Bearer JWT (CI platform OIDC)
- [x] `IJwksCache` service: per-issuer JWKS fetch and cache (1h TTL); force-refresh on unknown `kid` (key rotation)
- [x] `ITrustEvaluator` service: evaluate `ciTrust` bindings against JWT claims — glob matching, ordered first-match, audience enforcement
- [x] `IOptionsMonitor<AuthProxyConfig>` hot-reload from ConfigMap — no sidecar restart required on binding changes
- [x] Operator renders auth proxy ConfigMap from `ciTrust` spec and injects `maven-auth-proxy` sidecar into NGINX pods when `ciTrust` is non-empty
- [x] `NginxConfigRenderer`: renders `auth_request /auth/validate` block (replaces `auth_basic` when `ciTrust` is non-empty)
- [x] ACL location block rendering (ordered by longest-prefix-first)
- [x] Unit tests: `TrustEvaluator` — GitHub Actions claims, GitLab CI claims, glob wildcards, first-match semantics, empty-claims rejection, audience mismatch → 403
- [x] Unit tests: `JwksCache` — cache hit path, cache miss (HTTP fetch), force-refresh on unknown kid, HTTPS-only issuer URL
- [x] Unit tests: `RoleBasedHtpasswdService` — role filtering correctness
- [x] Unit tests: ACL `location` block specificity ordering
- [x] Integration tests: synthetic GitHub-format pre-signed JWT → 200 with correct role; wrong `repository` claim → 403; expired JWT → 401; unknown issuer → 403
- [x] Integration tests: synthetic GitLab-format pre-signed JWT → 200 with correct role; `ref_protected: false` when binding requires `true` → 403
- [x] E2E tests: GitHub-format JWT in Authorization header → `mvn deploy` succeeds end-to-end; wrong repo claim → 403 from NGINX
- [x] Backward-compatibility: existing `auth.download.secretRefs` still works without any `ciTrust` bindings

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
- [x] `spec.storage.accessMode` defaults to `ReadWriteMany`; CEL validates enum; admission warns when StorageClass is RWO-only

### New CRD: `MavenRepositoryImport`
- [x] CRD entity + spec/status classes; CEL: exactly one of `source.api`, `source.pvcSnapshot`, `source.pvcLive`
- [x] `MavenRepositoryImportController` — validates target repo, resolves transfer mode, launches Job, syncs status, finalizer

### `MavenOperator.ImportJob` console app — three transfer modes

**Mode A — API crawl + direct PVC write**
- [x] `ReposiliteApiSource`: recursive BFS via Reposilite REST API; `sinceTimestamp`; Polly retry
- [x] `JFrogCloudApiSource`: flat-list Artifactory storage API; Bearer-token auth; group filters
- [x] `DirectPvcSink`: write artifact bytes directly to mounted target repo PVC (no HTTP hop)
- [x] `HttpSink` (fallback): HTTP PUT when target PVC is RWO and already claimed; operator emits `Warning`
- [x] `PvcAccessChecker`: detect RWO conflicts before Job launch; resolve `transferMode: auto`

**Mode B — Snapshot / external PVC clone**
- [x] `PvcSnapshotSource`: filesystem walk of mounted source PVC; `reposiliteLayout` path stripping; `mtime` filter
- [x] Operator mounts source PVC (RO) + target PVC (RW) into Job; skips `maven-metadata.xml`
- [x] Abort with `Failed` + `Error` condition when source PVC is RWO-bound to a running pod

**Mode C — Live Reposilite PVC clone**
- [x] Operator stores replica count in `maven.operator.io/pre-import-replicas` annotation; scales Deployment to 0 when `scaleDownDuration > 0s`
- [x] Finalizer `maven.operator.io/import-cleanup`: always restores Reposilite replicas on CR deletion
- [x] Concurrent mode (`scaleDownDuration: 0s`): requires RWX PVC; `Warning` condition emitted

**Shared**
- [x] `MavenLayoutTranslator`: strip `/<repository>/` prefix; normalise separators; remove `.index`/`.cache` dirs
- [x] `ProgressReporter`: patch `status.artifactsCopied` on parent CR from inside Job
- [x] `ArtifactCrawler`: bounded parallelism (`options.parallelism`); error isolation per artifact
- [x] `ChecksumValidator`: SHA-256 post-write verification (optional)

### Performance Comparison — k6
- [x] `k6/comparison/maven-operator.js` + `reposilite.js` — 5 scenarios (download-small, download-large, upload, metadata, mixed)
- [x] `k6/comparison/compare.sh` — side-by-side `summary.json`
- [x] CI `performance-comparison` job: seed via snapshot import, run compare.sh, gate on p50/p95/throughput/error-rate
- [x] BenchmarkDotNet `ImportThroughputBenchmark`: `DirectPvcSink` ≥ 3× `HttpSink` throughput

### Tests
- [x] Unit: `ReposiliteApiSourceTests`, `JFrogCloudApiSourceTests`, `PvcSnapshotSourceTests`, `DirectPvcSinkTests`, `HttpSinkTests`, `ArtifactCrawlerTests`, `ChecksumValidatorTests`, `MavenLayoutTranslatorTests`, `PvcAccessCheckerTests`
- [x] Integration (Mode A): direct-write with RWX PVC; WireMock JFrog; RWO fallback to HTTP
- [x] Integration (Mode B): Reposilite layout stripping; raw Maven layout; overwrite-skip; dry-run; RWO conflict abort
- [x] Integration (Mode C): scale-down/restore; finalizer scale-up; concurrent RWX
- [x] E2E: `ApiImportReposiliteE2ETest`, `SnapshotImportE2ETest`, `LivePvcImportE2ETest`, `DryRunE2ETest`, `PartialMigrationWithFiltersE2ETest`

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
- [x] `GatewaySpec.cs` + `GatewayRefSpec.cs` entity classes added to `Entities/Spec/`
- [x] `spec.gateway` sub-spec added to `MavenRepositorySpec`
- [x] CRD YAML updated in `config/crds/` and `charts/maven-operator/crds/` (in sync)
- [x] CEL rules: mutual exclusion with `spec.ingress`, required `gatewayRef.name`

### Reconciler
- [x] `GatewayRouteReconciler` service — idempotently creates/patches/deletes `HTTPRoute`
- [x] Hosted / Proxy: single `PathPrefix` rule → `<name>-svc`
- [x] Virtual: additional rule matching PUT / DELETE returning 405 (gateway-native or NGINX fallback)
- [x] Owner reference set on `HTTPRoute`
- [x] `status.url` derived from hostname + path (https:// when `tlsSecretRef` set)

### RBAC
- [x] `httproutes` verb rules added to Helm `ClusterRole` in `rbac.yaml`

### Tests
- [x] Unit: `GatewayRouteReconciler` — enabled/disabled, each repo type, missing `gatewayRef.name`
- [x] Integration: operator creates / updates / deletes `HTTPRoute` on CRD changes
- [x] E2E: Maven client resolves artifacts through a Gateway API HTTPRoute

**Done when:** A `MavenRepository` with `spec.gateway.enabled: true` causes the operator
to create a valid `HTTPRoute`; toggling back to `spec.gateway.enabled: false` deletes it;
and `mvn dependency:resolve` succeeds through the route in a k3d cluster with Envoy Gateway.

