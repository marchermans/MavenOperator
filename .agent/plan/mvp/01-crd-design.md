# 01 — CRD Design
## Group / Version / Kind
| Field | Value |
|-------|-------|
| Group | `maven.operator.io` |
| Version | `v1alpha1` (MVP) → `v1beta1` → `v1` |
| Kind | `MavenRepository` |
---
## MavenRepository CRD Schema
```yaml
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepository
metadata:
  name: my-releases
  namespace: maven
spec:
  # ── Repository type ────────────────────────────────────────────────
  type: Hosted          # Hosted | Proxy | Virtual
  # ── Storage (only for Hosted) ─────────────────────────────────────
  storage:
    size: 50Gi
    storageClassName: standard   # optional, uses cluster default if absent
    deletionPolicy: Retain       # Retain | Delete (default: Retain)
  # ── Upstream (only for Proxy) ─────────────────────────────────────
  upstream:
    url: https://repo1.maven.org/maven2
    cacheTtl: 1d                 # optional, default 1d
    auth:                        # optional — single upstream credential
      secretRef: maven-central-credentials
  # ── Members (only for Virtual) ────────────────────────────────────
  virtual:
    members:
      - my-releases
      - my-snapshots
      - central-proxy
  # ── Authentication ────────────────────────────────────────────────
  auth:
    # download: who can download artifacts
    download:
      policy: Anonymous          # Anonymous | Authenticated
      # secretRefs only used when policy == Authenticated
      secretRefs:
        - reader-credentials
        - ci-bot-credentials
    # upload: who can upload artifacts (Hosted only; Virtual always returns 405)
    upload:
      policy: Authenticated
      secretRefs:
        - deployer-credentials   # one or more users — all are compiled into htpasswd
        - admin-credentials
  # ── Exposure (choose one; not both) ──────────────────────
  ingress:
    enabled: true
    host: maven.example.com
    path: /repository/my-releases
    tlsSecretRef: maven-tls      # optional
  # — OR — Kubernetes Gateway API
  gateway:
    enabled: false               # mutually exclusive with ingress.enabled
    gatewayRef:
      name: prod-gateway         # required when enabled
      namespace: infra-gateways # optional; defaults to MavenRepository namespace
      sectionName: https         # optional
    hostname: maven.example.com  # optional
    path: /repository/my-releases # optional; defaults to /repository/{name}
    tlsSecretRef: maven-tls      # optional; controls http vs https in status.url
    routeLabels: {}              # extra labels on the HTTPRoute
    routeAnnotations: {}         # extra annotations on the HTTPRoute
  # ── Resource limits ───────────────────────────────────────────────
  resources:
    requests:
      cpu: 100m
      memory: 128Mi
    limits:
      cpu: 500m
      memory: 512Mi
```
---
## Credential Secret Format (per user, one Secret per user)
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: deployer-credentials
  namespace: maven
  labels:
    maven.operator.io/credential: "true"   # required — operator watches this label
type: Opaque
stringData:
  username: deployer
  password: s3cr3t
  # Do NOT include htpasswd — the operator generates and manages the hash
```
Each Secret holds exactly **one** user's credentials. The operator compiles all secrets
referenced by a repository's `auth.download.secretRefs` and `auth.upload.secretRefs` into
two separate htpasswd files.
---
## Status Sub-resource
```yaml
status:
  phase: Ready              # Pending | Provisioning | Ready | Degraded | Failed
  url: http://maven.example.com/repository/my-releases
  conditions:
    - type: Available
      status: "True"
      lastTransitionTime: "2026-05-15T10:00:00Z"
      reason: DeploymentReady
      message: "NGINX deployment is ready"
    - type: StorageBound
      status: "True"
      reason: PVCBound
      message: "PVC my-releases-pvc is bound"
    - type: AuthReady
      status: "True"
      reason: HtpasswdGenerated
      message: "2 download users, 2 upload users configured"
  observedGeneration: 3
```
---
## Validation Rules (CEL)
- `type == "Hosted"` → `storage` required, `virtual` forbidden.
- `type == "Proxy"` → `upstream.url` required, `storage` and `virtual` forbidden.
- `type == "Virtual"` → `virtual.members` required (min 1), `storage` and `upstream` forbidden.
- `virtual.members` must not contain the CRD's own name (loop prevention).
- `auth.download.policy == "Authenticated"` → `auth.download.secretRefs` must be non-empty.
- `auth.upload.policy == "Authenticated"` → `auth.upload.secretRefs` must be non-empty.
- `auth.upload` is only meaningful when `type == "Hosted"` (Proxy/Virtual ignore it).
- Usernames within a single `secretRefs` list must be unique (validated via admission webhook,
  since the operator must read the Secrets to check — CEL alone cannot do cross-resource lookups).
---
## Child Resource Naming Convention
All child resources: `<MavenRepository.name>-<suffix>` with owner references set.
| Suffix | Kind | Purpose |
|--------|------|---------|
| `-nginx` | Deployment | NGINX pod |
| `-proxy` | Deployment | C# aggregation proxy (Virtual only) |
| `-svc` | Service | ClusterIP service |
| `-pvc` | PersistentVolumeClaim | Storage (Hosted only) |
| `-nginx-cm` | ConfigMap | NGINX configuration |
| `-download-htpasswd` | Secret | Compiled htpasswd for download users (operator managed) |
| `-upload-htpasswd` | Secret | Compiled htpasswd for upload users (operator managed) |
| `-ing` | Ingress | Classic Ingress (when `spec.ingress.enabled: true`) |
| `-route` | HTTPRoute | Gateway API HTTPRoute (when `spec.gateway.enabled: true`) |
