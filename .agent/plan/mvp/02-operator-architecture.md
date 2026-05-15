# 02 — Operator Architecture
## Technology Stack
| Component | Choice | Rationale |
|-----------|--------|-----------|
| Language | C# .NET 10 | Existing project stack |
| Operator SDK | [KubeOps](https://github.com/buehler/dotnet-operator-sdk) | First-class .NET K8s operator SDK |
| HTTP Framework | ASP.NET Core Minimal APIs | Already scaffolded, AOT-friendly |
| CRD codegen | KubeOps generators | Auto-generates CRD YAML from C# attributes |
| Container | `mcr.microsoft.com/dotnet/aspnet:10.0` | Runtime image |
---
## Project Structure
```
MavenOperator/
  Controllers/
    MavenRepositoryController.cs   # Main reconciliation loop
  Entities/
    MavenRepositoryV1Alpha1.cs     # CRD entity (KubeOps annotated)
    Spec/
      MavenRepositorySpec.cs
      AuthSpec.cs
      StorageSpec.cs
      UpstreamSpec.cs
      VirtualSpec.cs
      IngressSpec.cs
    Status/
      MavenRepositoryStatus.cs
  Reconcilers/
    HostedRepositoryReconciler.cs
    ProxyRepositoryReconciler.cs
    VirtualRepositoryReconciler.cs
  Services/
    NginxConfigRenderer.cs         # Renders NGINX config templates
    HtpasswdService.cs             # Generates htpasswd from username/password
    KubernetesResourceManager.cs   # Creates/patches K8s child resources
    MetadataMergeService.cs        # maven-metadata.xml merging (for Virtual)
  Templates/
    nginx-hosted.conf.template
    nginx-proxy.conf.template
    nginx-virtual.conf.template
  Program.cs
```
---
## Reconciliation Loop
```
MavenRepositoryController.ReconcileAsync(MavenRepositoryV1Alpha1 entity)
│
├─ Set status.phase = Provisioning
│
├─ Switch entity.Spec.Type
│   ├─ Hosted  → HostedRepositoryReconciler.ReconcileAsync(entity)
│   ├─ Proxy   → ProxyRepositoryReconciler.ReconcileAsync(entity)
│   └─ Virtual → VirtualRepositoryReconciler.ReconcileAsync(entity)
│
├─ Set status.phase = Ready (or Degraded/Failed on error)
└─ Update status conditions
```
Each reconciler follows this pattern:
1. **Ensure PVC** (Hosted only) — create if missing, expand if size changed.
2. **Ensure Secret** (htpasswd) — generate from referenced credentials Secret.
3. **Ensure ConfigMap** — render NGINX config template with current spec values.
4. **Ensure Deployment** — create or patch NGINX/proxy Deployment.
5. **Ensure Service** — create ClusterIP Service.
6. **Ensure Ingress** — create/update if `ingress.enabled == true`.
All "Ensure" operations use **server-side apply** (`kubectl apply` semantics) so they are idempotent.
---
## Watch Events
| Event | Action |
|-------|--------|
| `Added` | Full reconcile |
| `Modified` | Full reconcile (idempotent) |
| `Deleted` | Owner references handle child GC automatically |
| Referenced Secret changes | Trigger reconcile of dependent MavenRepository |
To handle Secret changes triggering reconciliation, the controller registers a secondary watch on Secrets filtered by label `maven.operator.io/managed-by`.
---
## Error Handling & Retry
KubeOps provides built-in requeueing. The controller:
- Returns `RequeueAfter(30s)` on transient errors (API unavailable, etc.)
- Emits a Kubernetes Event describing the failure reason.
- Sets `status.conditions` with `Available=False` and the error message.
- Does NOT requeue on permanent validation errors (misconfigured CRD) — surfaces the error in status and waits for user correction.
