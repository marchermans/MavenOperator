# 02 — Virtual Repository Upload Fan-Out

## Concept

Virtual repositories currently fan **in** downloads from multiple members (Hosted or Proxy).
With upload support, they can also fan **out** uploads to a configurable subset of those members.

Key design decision: **Upload targets are explicitly declared**, not inferred from all members.
This prevents accidental writes to read-only proxies or unintended repositories.

## Authentication Model

Virtual repository uploads use the **same auth model as Hosted repositories**:

- Separate `auth.upload` policy with its own users/roles.
- Client authenticates against the Virtual repo's upload htpasswd (or auth proxy).
- The operator then forwards the artifact to each target member using that member's credentials.

### Why not pass client credentials upstream?

1. Members may use different credential stores — a user valid on the Virtual repo might not exist on a member.
2. Proxy members often have fixed server-to-server auth, not per-user auth.
3. Simplifies migration: one set of deployer credentials for the Virtual URL, operator handles routing.

## Upload Target Selection

### CRD Structure

```yaml
apiVersion: maven.operator.io/v1
kind: MavenRepository
metadata:
  name: my-virtual-releases
spec:
  type: Virtual
  virtual:
    members:
      - name: releases-hosted-a
        type: Hosted
      - name: releases-hosted-b
        type: Hosted
      - name: central-proxy
        type: Proxy
    uploadTargets:          # NEW — explicit list of repos to write to on deploy
      - releases-hosted-a   # primary storage
      - releases-hosted-b   # backup / DR copy
```

### Rules

1. **Hosted and Proxy members can be upload targets**:
   - Hosted: artifact stored directly on PVC via WebDAV.
   - Proxy: artifact forwarded upstream using that Proxy's own `upload.upstreamCredentialsRef`.
2. **Proxy targets require upload forwarding enabled** — a Proxy member can only be an upload target if its own `spec.proxy.upload.enabled` is true. The Virtual proxy uploads to the Proxy's internal endpoint, and the Proxy handles forwarding to its upstream (e.g., legacy Nexus).
3. **Targets must be declared members** — you cannot upload to a repo that isn't already a Virtual member.
4. **At least one target required** — `uploadTargets: []` means uploads are disabled (405).
5. **No duplicate targets** — validated at admission time.

### Validation (Webhook)

The admission webhook must verify:
- Each entry in `uploadTargets` exists in `members`.
- Each target member is either `Hosted` or `Proxy`.
- If a target is `Proxy`, that Proxy repo's CRD has `spec.proxy.upload.enabled == true`.
- No duplicates in the list.

If validation fails, reject with a clear error message (e.g., "Upload target 'central-proxy' is a Proxy without upload forwarding enabled").

## Upload Flow

```
Client (mvn deploy)
    │
    ▼  PUT /repository/my-virtual-releases/com/example/artifact/1.0/artifact-1.0.jar
Virtual Proxy (C# aggregation service)
    │
    ├─ Authenticate client against upload auth policy
    │   └─ Fail → 401 Unauthorized
    │
    ├─ Read uploadTargets from CRD spec
    │
    ├─ For each target in parallel:
    │     ├─ Resolve target's internal URL (e.g., http://releases-hosted-a.svc.cluster.local)
    │     ├─ Inject target's deploy credentials (from operator-managed Secret or derived auth)
    │     └─ PUT /com/example/artifact/1.0/artifact-1.0.jar
    │
    ▼  Aggregate results:
        - All succeeded → return first 201 Created to client
        - Some failed   → return 207 Multi-Status (or 502 with details in body/logs)
        - All failed    → return most severe error code
```

## Credential Strategy for Upstream Members

The Virtual proxy needs credentials to upload to each target member. Three strategies are supported:

### Strategy 1: Shared Auth Secret (Recommended)

A single Secret contains credentials valid for **all** target members:

```yaml
virtual:
  uploadTargets:
    - releases-hosted-a
    - releases-hosted-b
  uploadCredentialsRef:
    name: virtual-deployer-creds
```

This works when all targets share the same user/password (common in homogeneous clusters).

### Strategy 2: Per-Member Credentials

Each target can have its own credential Secret:

```yaml
virtual:
  uploadTargets:
    - name: releases-hosted-a
      credentialsRef:
        name: hosted-a-deployer-creds
    - name: releases-hosted-b
      credentialsRef:
        name: hosted-b-deployer-creds
```

Use this when targets have different auth configurations (e.g., multi-cluster or federated setups).

### Strategy 3: Operator-Derived Credentials (Advanced)

The operator automatically creates a deployer user on each target Hosted repo and injects it
into the Virtual proxy. This is the most seamless but requires:

- Target repos to be managed by the same operator instance.
- The operator to have permission to modify auth policies on targets.

This is a **future enhancement** — not in scope for initial implementation.

## Implementation Details

### C# VirtualProxy Changes

The existing `VirtualProxyService` only handles GET requests. Upload support requires:

1. **New endpoint handler** for PUT/DELETE/MKCOL methods.
2. **Upload fan-out service** (`VirtualUploadService`) that:
   - Reads the current CRD spec (via Kubernetes client or injected config).
   - Resolves target member URLs and credentials.
   - Sends parallel upload requests with Polly retry policy.
   - Aggregates responses into a single result.

### Request Routing

The Virtual proxy already routes based on repository path prefix:
```
/repository/my-virtual-releases/... → VirtualProxy for "my-virtual-releases"
```

Uploads use the same routing — no NGINX changes needed beyond allowing PUT/DELETE through to the C# proxy.

### NGINX Template Changes (Virtual)

Current (blocks writes):
```nginx
if ($request_method !~ ^(GET|HEAD)$) {
    return 405;
}
```

With uploads enabled:
```nginx
{{ if upload_enabled }}
# Allow all Maven methods through to the C# proxy — it handles auth and routing.
{{ else }}
if ($request_method !~ ^(GET|HEAD)$) {
    return 405 "Virtual repository does not accept uploads.";
}
{{ end }}

proxy_pass http://127.0.0.1:5000;
```

The C# proxy becomes the single source of truth for upload behavior.

### Response Aggregation Rules

| Scenario | HTTP Status to Client | Behavior |
|----------|----------------------|----------|
| All targets succeed (200/201) | 201 Created | Return first success response; log all successes. |
| Some targets fail, at least one succeeds | 207 Multi-Status | Include JSON body listing per-target results. Log failures. |
| All targets return 4xx | Most severe 4xx | Aggregate error messages in response body. |
| All targets return 5xx or timeout | 502 Bad Gateway | "All upload targets failed — check repository configuration." |

### maven-metadata.xml on Upload

When an artifact is uploaded, the target Hosted repo's NGINX (with WebDAV) automatically handles
directory creation and file placement. The `maven-metadata.xml` update is a **separate concern**:

- Option A: Let each Hosted member manage its own metadata independently (simplest).
- Option B: Virtual proxy triggers a metadata refresh on each target after upload (more complex).

**Decision:** Start with Option A. Metadata merging for reads already handles inconsistencies.
Option B can be added later if users report stale metadata issues.

## Error Handling and Observability

### Client-Facing Errors

| Condition | Response |
|-----------|----------|
| No `uploadTargets` configured | 405 "Virtual repository does not accept uploads." |
| Client auth fails | 401 Unauthorized |
| Target member not found (deleted) | 503 "Upload target 'X' is unavailable — check Virtual repo configuration." |
| Credential Secret missing | 500 "Internal error — upload credentials not configured." (logged with details) |

### Logging

Every upload attempt must log:
- Client identity (if authenticated).
- Artifact path and version.
- Each target's result (success/failure, HTTP status, latency).
- Any credential resolution errors.

Example structured log:
```json
{
  "event": "virtual_upload",
  "repository": "my-virtual-releases",
  "artifact": "com/example/artifact/1.0/artifact-1.0.jar",
  "client_user": "deployer",
  "targets": [
    {"name": "releases-hosted-a", "status": 201, "latency_ms": 45},
    {"name": "releases-hosted-b", "status": 500, "error": "Internal Server Error"}
  ],
  "overall_status": 207
}
```

## Status Reporting

New status fields for Virtual repos with uploads:

```yaml
status:
  upload:
    enabled: true
    targets:
      - name: releases-hosted-a
        reachable: true
        lastUploadSuccess: "2026-08-09T18:30:00Z"
      - name: releases-hosted-b
        reachable: false
        lastError: "Connection refused — pod not running"
```

The operator periodically probes each target (e.g., HEAD request) to update `reachable`.

## Example Use Case — Migration from Legacy Maven

A team migrates from Nexus to the operator-managed Maven setup but wants a rollback path:

```yaml
# 1. Mount legacy Nexus as a Proxy with upload forwarding enabled
apiVersion: maven.operator.io/v1
kind: MavenRepository
metadata:
  name: nexus-legacy-proxy
spec:
  type: Proxy
  proxy:
    upstreamUrl: https://nexus.internal/repository/maven-releases/
    upstreamAuth:
      secretRef: nexus-reader-creds
    upload:
      enabled: true
      mode: Passthrough
      upstreamCredentialsRef:
        name: nexus-deployer-creds

# 2. Create new Hosted repo for future artifacts
apiVersion: maven.operator.io/v1
kind: MavenRepository
metadata:
  name: releases-hosted-new
spec:
  type: Hosted
  auth:
    upload:
      policy: Authenticated
      users:
        - secretRef: team-deployer-creds
          role: Deployer

# 3. Virtual combines both — reads from all, uploads to both
apiVersion: maven.operator.io/v1
kind: MavenRepository
metadata:
  name: releases-virtual
spec:
  type: Virtual
  auth:
    upload:
      policy: Authenticated
      users:
        - secretRef: team-deployer-creds
          role: Deployer
  virtual:
    members:
      - name: nexus-legacy-proxy   # Proxy to legacy Nexus (read + write)
        type: Proxy
      - name: releases-hosted-new  # New operator-managed storage (read + write)
        type: Hosted
    upload:
      targets:
        - name: releases-hosted-new     # primary: new storage
        - name: nexus-legacy-proxy      # fallback: still writes to Nexus during migration
      sharedCredentialsRef:
        name: internal-deployer-creds   # credentials valid for both targets' internal endpoints
```

Now `mvn deploy` to the Virtual URL:
1. Stores artifact on the new Hosted repo (PVC-backed).
2. Forwards artifact through the Proxy to legacy Nexus.

This allows zero-downtime migration with a rollback path — if the new setup fails, Nexus still has all artifacts. Once confident, remove `nexus-legacy-proxy` from upload targets (reads can continue for cache hits), then eventually decommission Nexus entirely.
