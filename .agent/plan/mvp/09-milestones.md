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
- LDAP / OIDC authentication (planned for v1beta1).
- `MavenRepositoryBackup` CRD.
- Web UI / dashboard.
- Artifact search / indexing.
- Quota enforcement per repo.
