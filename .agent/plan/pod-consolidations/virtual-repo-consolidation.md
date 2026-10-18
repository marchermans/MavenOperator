# Plan: Collapse Virtual Repository to a single pod (nginx + proxy sidecar)

Status: **implementing** — baseline `8496edc`

## Target shape

One `{name}-nginx` Deployment per Virtual repo, mirroring the Hosted repos' sidecar pattern:

```
pod ┌─────────────┐  loopback:8080  ┌──────────────────┐
    │ nginx (:80)  │ ─────────────► │ C# proxy (:8080)  │
    └─────────────┘                 └──────────────────┘

{name}-svc :80 → nginx            {name}-proxy-svc :8080 → proxy (upload-status polling only)
```

Previously: two Deployments (`{name}-nginx`, `{name}-proxy`) plus an intermediate
`{name}-proxy-svc` ClusterIP so nginx could reach the proxy pod.

## Why this is clean

- The merged Deployment keeps the name `{name}-nginx`, so both Services'
  `app={name}-nginx` selectors and any Ingress/Gateway wiring are untouched.
- k8s only routes traffic to a pod when **all** containers are Ready → the
  startup window where nginx answered 502 for an unready proxy disappears for
  free. The E2E fixtures' separate proxy-readiness waits become redundant — and
  would break, because they poll label `app={name}-proxy`, which no longer exists.
- The operator polls upload-status from outside the pod
  (`{name}-proxy-svc.<ns>.svc.cluster.local:8080/api/virtual/upload-status`), so we
  keep that Service retargeted at the merged deployment and bind the proxy on all
  interfaces — no redesign of the status path needed.

## Changes by file

### 1. `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs`
- Merge `BuildProxyPodSpec()` + `BuildNginxPodSpec()` into one
  `BuildVirtualPodSpec()`: containers `[nginx, proxy]`, volumes =
  `..BuildNginxVolumes(name)` + `proxy-config`.
  - nginx keeps user-specified `spec.resources`; the proxy sidecar gets a fixed
    modest budget (50m/64Mi requests → 200m/256Mi limits), same convention as Hosted.
- Replace the two `EnsureDeploymentAsync` calls with one dict-hash call on
  `{name}-nginx`: `{"nginx": hash(nginxConfig + htpasswds), "proxy-config":
  hash(proxyConfigJson)}` — per-component observability; any change rolls the
  single deployment (same pattern as Hosted).
- Retarget both Services at the merged deployment:
  `EnsureServiceAsync(entity, $"{name}-proxy-svc", deploymentName, ProxyPort)` and
  `{name}-svc`.
- **One-shot migration** in `CleanupObsoleteResourcesAsync`:
  `DeleteResourceIfExistsAsync<V1Deployment>($"{name}-proxy", ns, ct)`.

### 2. `MavenOperator/Services/KubernetesResourceManager.cs` — required fix
- `EnsureServiceWithPortsAsync` early-returns when *ports* match but never checks
  the selector. The legacy `{name}-proxy-svc` has port 8080 (matches) and selector
  `app={name}-proxy` (stale after merge) → it would silently keep pointing at
  deleted pods, breaking upload-status polling on existing repos. Add a
  selector-match check (`spec.selector["app"] == expected`) to the early-return
  condition so the service gets retargeted in place.

### 3. `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs` (nginx render)
- `proxy_pass http://{{name}}-proxy-svc:8080/` → `http://127.0.0.1:8080/`.
  Keep the URI-form proxy_pass — do **not** switch to `rewrite … break;` (a
  limit_except block silently suppresses rewrites for unlisted methods, which
  corrupted upload fan-out paths).

## Migration behavior (zero-downtime)

First reconcile after operator upgrade: component hashes change → rolling update
of the *existing* `{name}-nginx` deployment to the 2-container template (surge=1
default); `CleanupObsoleteResourcesAsync` deletes the standalone `{name}-proxy`
deployment. No per-repo intervention, no manual steps.

## Test updates

- **Integration** (`VirtualRepositoryReconcilerTests.cs`): replace
  `Reconcile_CreatesProxyDeployment` with one test asserting a single deployment
  with exactly 2 containers (nginx + proxy image), the per-component hash
  annotations, and `{name}-proxy` absent. Drop the proxy-deployment ownership
  assertion; service assertions stay as-is (ports unchanged).
- **E2E** (`VirtualE2EFixture.cs`, `UploadE2EFixture.cs`,
  `ExternalUploadTargetE2ETests.cs`): delete the bespoke proxy-readiness waits —
  `PodReadinessHelper.WaitForNginxReadyAsync` already requires *all* containers
  of the pod Ready, which now covers the sidecar. Leaving them in would hang →
  timeout.

## Verification

- `dotnet build` the solution.
- Run the virtual integration suite against k3d (`INTEGRATION_TESTS=true`).
- Ideally exercise the migration path with a pre-existing legacy 2-deployment
  repo (old proxy deployment present before operator upgrade).

## Trade-offs / accepted risks

- Proxy crash/OOM restarts just that container, but takes the whole pod
  NotReady → both Services drop endpoints → clients see ingress-level errors
  instead of nginx's 502 fallback. Brief unavailability comparable to today's
  proxy-pod restart; arguably a cleaner signal.
- Slightly wider in-pod exposure of :8080 — still cluster-only via ClusterIP,
  same as today.
- Default footprint drops from ~256Mi/1Gi (two pods) → ~160Mi/768Mi (one pod).
