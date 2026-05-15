# MavenOperator — Copilot Instructions
## What this project is
A **Kubernetes Operator** written in **C# .NET 10** (ASP.NET Core Minimal APIs) that manages
self-hosted Maven repositories via Custom Resource Definitions (CRDs). The operator provisions
and reconciles NGINX-based repository pods and a C# aggregation proxy for virtual repositories.
The full plan lives in `.agent/plan/mvp/`. Always consult it before making architectural decisions.
---
## Core Tenants
### 1. CRD is the single source of truth
Every aspect of a Maven repository's desired state is expressed in a `MavenRepository` CRD.
Never hardcode configuration — always derive it from the CRD spec.
### 2. Three repository types only
- **Hosted** — stores artifacts on a PersistentVolume, served by NGINX + WebDAV.
- **Proxy** — caches a remote upstream via NGINX `proxy_pass`.
- **Virtual** — fans out to multiple members; served by the C# aggregation proxy.
No other types exist. Validate this at admission time.
### 3. Multi-user authentication from day one
Auth policies use a **list** of `secretRefs`, never a single `secretRef`.
Each Secret holds exactly one user (`username` + `password`).
The operator compiles all referenced Secrets into one htpasswd file per policy.
Download and upload policies are **independent** and produce **separate** htpasswd files:
  - `<name>-download-htpasswd` Secret
  - `<name>-upload-htpasswd` Secret
### 4. Uploads to Virtual repositories are forbidden
Virtual repositories return `405 Method Not Allowed` on PUT/DELETE.
Uploads must go directly to a Hosted member. Always surface a helpful error message.
### 5. maven-metadata.xml must be merged for Virtual repos
Never return the first-found metadata response. Always fetch from **all** members in parallel
and merge using the rules in `.agent/plan/mvp/05-virtual-repo-metadata.md`.
### 6. Operator-managed secrets — users never write htpasswd
The operator owns all `-download-htpasswd` and `-upload-htpasswd` Secrets.
Users only write `username` / `password` into their credential Secrets.
The operator hashes and rebuilds htpasswd whenever a credential Secret changes.
### 7. Idempotent reconciliation
Every reconciler step must be safe to run multiple times. Use server-side apply.
Never assume a resource doesn't exist — always check and patch, not blindly create.
### 8. Owner references on all child resources
Every Kubernetes resource the operator creates must have an owner reference pointing to the
parent `MavenRepository` CRD, except PVCs (which follow `spec.storage.deletionPolicy`).
### 9. PVCs are Retain by default
Deleting a `MavenRepository` CRD must NOT automatically delete artifact storage unless
`spec.storage.deletionPolicy: Delete` is explicitly set. Default is `Retain`.
### 10. Surface errors in status, not just logs
Reconciliation failures must be reflected in `status.phase` and `status.conditions`.
Emit a Kubernetes Event for every meaningful state change or error.

### 11. Testability is a first-class requirement
Testing is the primary validation metric for every feature delivered in this project.
Every layer must be independently testable:

- **Unit tests** — all services (`HtpasswdService`, `NginxConfigRenderer`, `MetadataMergeService`, etc.)
  must be pure, dependency-injected, and testable without a cluster or running containers.
- **Integration tests** — reconcilers are tested against a real Kubernetes API using
  `envtest` / `k3d` with actual CRD apply/update/delete cycles.
- **E2E tests** — full Maven client (`mvn deploy`, `mvn dependency:resolve`) scenarios
  against an operator-provisioned cluster, covering all three repo types and all auth policies.
- **Performance tests** — metadata merge latency, proxy throughput, and reconcile loop
  timing are benchmarked and tracked over time. No feature ships without a baseline.

Design code so it can be tested: prefer constructor injection, avoid static state,
keep reconciler steps small and individually invokable. If something is hard to test,
that is a design smell — fix the design, not the test.
---
## Technology Choices (do not change without updating the plan)
| Concern | Choice |
|---------|--------|
| Language | C# .NET 10 |
| Operator SDK | KubeOps |
| Web framework | ASP.NET Core Minimal APIs |
| NGINX image | `nginx:1.27-alpine` |
| Config templating | Scriban (or simple string interpolation) |
| Metadata cache | `IMemoryCache` (in-process, per Virtual proxy pod) |
| Version comparison | `NuGet.Versioning` |
| HTTP resilience | Polly |
---
## Project Layout
```
MavenOperator/
  Controllers/        # KubeOps reconciliation controllers
  Entities/           # CRD entity + Spec/Status classes
  Reconcilers/        # One reconciler per repo type
  Services/           # NginxConfigRenderer, HtpasswdService, MetadataMergeService, ...
  Templates/          # NGINX config templates
```
---
## Delivery Phases (see 09-milestones.md for details)
| Phase | Goal |
|-------|------|
| 0 | KubeOps wired up, CRD entity defined, operator running |
| 1 | Hosted repository end-to-end (mvn deploy + resolve) |
| 2 | Proxy repository (cache from Maven Central) |
| 3 | Virtual repository (fan-out + metadata merge) |
| 4 | Hardening (CEL validation, Events, Secret watch, deletionPolicy) |
| 5 | Observability + Helm chart + CI |
