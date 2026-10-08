# 04 — Implementation Plan

Phased rollout of upload support for Proxy and Virtual repositories. Each phase is independently testable and deployable.

---

## Phase 1: CRD and Entity Changes

**Goal:** Add all new types to the CRD without changing runtime behavior.

### Tasks

- [x] Define `ProxyUploadMode` enum in `Entities/Enums/`.
- [x] Create `LocalObjectReference`, `ProxyUploadSpec` in `Entities/Spec/ProxySpec.cs`.
- [x] Create `VirtualUploadTarget`, `VirtualUploadSpec` in `Entities/Spec/VirtualSpec.cs`.
- [x] Add `Upload` property to `ProxySpec` and `VirtualSpec`.
- [x] Create upload status types (`ProxyUploadStatus`, `VirtualUploadStatus`, etc.) in `Entities/Status/`.
- [x] Add `Upload` property to `MavenRepositoryStatus`.
- [x] Update CRD YAML files:
  - `config/crds/mavenrepositories.maven.operator.io.yaml`
  - `charts/maven-operator/crds/mavenrepositories.maven.operator.io.yaml` (must be identical copy)
- [x] Run `./scripts/run-tests.sh all --fast` to verify no regressions.

### Acceptance Criteria

- Operator builds and deploys successfully.
- Existing CRDs apply without changes.
- New fields are optional with correct defaults.
- `kubectl explain mavenrepository.spec.proxy.upload` shows new schema.

---

## Phase 2: Proxy Upload — Core Reconciliation

**Goal:** Enable upload forwarding on Proxy repositories (internal-only, Passthrough mode).

### Tasks

- [x] Update `ProxyRepositoryReconciler`:
  - Read `spec.proxy.upload` configuration.
  - Watch and load upstream credentials Secret when enabled.
  - Set status fields (`upload.proxy.*`).
- [x] Add external exposure detection:
  - Check Service type (LoadBalancer/NodePort).
  - Query for Ingress/Gateway resources routing to this proxy's Service.
  - Block uploads if exposed and `forceAllowOnExternal == false`.
- [x] Update NGINX template (`Templates/nginx-proxy.conf.scriban`):
  - Add upload forwarding block for PUT/DELETE/MKCOL.
  - Inject upstream credentials as base64-encoded Basic auth header.
  - Support Passthrough mode (enforce client auth via existing upload policy).
- [x] Emit Events:
  - Normal: `UploadEnabled`, `UploadBlockedExternallyExposed`.
  - Warning: `UpstreamCredentialsMissing`, `UploadForwardingFailed`.

### Acceptance Criteria

- `mvn deploy` to a Proxy URL forwards the artifact to upstream.
- Client auth is enforced in Passthrough mode (401 without valid creds).
- Uploads are blocked on externally exposed proxies (status condition + event).
- GET/HEAD caching behavior unchanged.
- Integration test: apply Proxy CRD with uploads → verify NGINX config → deploy artifact → confirm upstream storage.

---

## Phase 3: Proxy Upload — Override Mode and Hardening

**Goal:** Add migration-friendly Override mode and polish error handling.

### Tasks

- [x] Implement `Override` mode in NGINX template:
  - Skip client auth for write methods when `mode == Override`.
  - Still forward with upstream credentials.
- [x] Improve error responses:
  - Map upstream 401/403 to 502 with helpful message ("Upstream rejected upload").
  - Never leak upstream credentials in error messages.
- [x] Add CEL validation (if webhook supports it):
  - `upstreamCredentialsRef` required when `enabled == true`.
  - `mode` must be valid enum value.
- [x] Update admission webhook:
  - Validate external exposure rules at apply time.
  - Reject invalid configurations early with clear messages.

### Acceptance Criteria

- Override mode allows unauthenticated uploads (for migration scenarios).
- Error messages are user-friendly and don't leak internals.
- Invalid CRDs are rejected by the webhook before reaching the reconciler.
- Run `./scripts/run-tests.sh all --fast` — all tests pass.

---

## Phase 4: Virtual Upload — Core Fan-Out Service

**Goal:** Enable upload fan-out on Virtual repositories to Hosted members.

### Tasks

- [x] Create `VirtualUploadService` in `MavenOperator.VirtualProxy/Services/`:
  - Reads current CRD spec (via injected config or K8s client).
  - Resolves target member URLs from cluster DNS (`<name>.<namespace>.svc.cluster.local`).
  - Loads credentials (shared or per-target) from Secrets.
  - Sends parallel PUT requests with Polly retry policy.
  - Aggregates responses into a single result (201/207/502).
- [x] Update `VirtualProxyService`:
  - Route PUT/DELETE/MKCOL to `VirtualUploadService` instead of returning 405.
  - Enforce client auth using existing upload policy before fan-out.
- [x] Update NGINX template (`Templates/nginx-virtual.conf.scriban`):
  - Allow write methods through when `upload.enabled == true`.
  - Keep 405 for Virtual repos without upload targets configured.
- [x] Add target reachability probing in `VirtualRepositoryReconciler`:
  - Periodic HEAD request to each upload target.
  - Update `status.upload.virtual.targets[].reachable`.

### Acceptance Criteria

- `mvn deploy` to a Virtual URL uploads to all declared targets (Hosted and Proxy).
- Client auth is enforced (same as Hosted repo).
- Partial failures return 207 with per-target details in logs.
- Proxy targets only accepted if they have upload forwarding enabled (webhook validation).
- Integration test: deploy to Virtual → verify artifact exists on all Hosted targets and upstream of Proxy targets.

---

## Phase 5: Virtual Upload — Credential Strategies and Status

**Goal:** Support all credential strategies and expose detailed status.

### Tasks

- [x] Implement per-target credential overrides (`VirtualUploadTarget.CredentialsRef`).
- [x] Implement shared credentials fallback (`VirtualUploadSpec.SharedCredentialsRef`).
- [x] Add validation in webhook:
  - Every target must have resolvable credentials (own ref or shared).
  - Targets must be declared members with type Hosted.
- [x] Update status reporting:
  - Track `lastUploadSuccess` per target (updated by VirtualProxy via a sidecar API or metrics endpoint).
  - Surface `lastError` when probing fails.

### Acceptance Criteria

- Mixed credential strategies work (some targets use shared, some use overrides).
- Status accurately reflects reachability of each upload target.
- Run `./scripts/run-tests.sh all --fast` — all tests pass.

---

## Phase 6: Observability and Testing

**Goal:** Ensure uploads are observable, testable, and production-ready.

### Tasks

- [x] Add structured logging for all upload operations (see 02-virtual-upload.md log format).
- [x] Expose metrics:
  - `maven_upload_requests_total{repository, target, status}`
  - `maven_upload_duration_seconds{repository, target}`
  - `maven_upload_target_reachable{repository, target}`
- [x] Add Grafana dashboard panels for upload activity (extend existing dashboards).
- [x] Write E2E tests:
  - Proxy upload forwarding with Passthrough and Override modes.
  - Virtual upload fan-out to multiple Hosted targets.
  - Partial failure scenarios (one target down, others succeed).
  - External exposure blocking on Proxy.
- [ ] Performance test: measure latency of Virtual uploads with N targets.

### Acceptance Criteria

- Upload activity is visible in logs and metrics.
- E2E tests pass against a real cluster (`cluster_apply_crds` + `kubectl`).
- No performance regression for read-only operations.
- Run `./scripts/run-tests.sh all --fast` — all tests pass.

---

## Dependencies Between Phases

```
Phase 1 (CRD) → Phase 2 (Proxy core) → Phase 3 (Proxy hardening)
                                    ↘ Phase 4 (Virtual core) → Phase 5 (Virtual creds)
                                                              ↘ Phase 6 (Observability + tests)
```

- Phases 2 and 4 can run in parallel after Phase 1.
- Phase 6 depends on both Proxy and Virtual implementations being complete.

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Upstream credentials leaked in logs/configmaps | Base64-encode in NGINX config; never log raw secrets; use Secrets, not ConfigMaps, for creds. |
| Virtual upload hangs if one target is slow | Use Polly with timeout per target (e.g., 30s); aggregate results non-blocking. |
| Accidental writes to wrong repos | Webhook validates targets are declared Hosted members; no dynamic resolution. |
| Breaking existing Proxy/Virtual behavior | All new fields default to disabled; opt-in only via explicit CRD changes. |

---

## Rollout Strategy

1. Deploy with all upload features **disabled by default**.
2. Enable in a non-production namespace first (staging).
3. Run E2E tests against staging cluster.
4. Gradually enable for production repos one at a time.
5. Monitor metrics and logs for anomalies during rollout.
