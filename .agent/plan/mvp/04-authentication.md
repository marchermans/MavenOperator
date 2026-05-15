# 04 — Authentication
## Authentication Matrix
| Repo Type | Download | Upload |
|-----------|----------|--------|
| Hosted | Anonymous or Authenticated | Authenticated (one or more users) |
| Proxy | Anonymous or Authenticated | N/A (upstream handles it) |
| Virtual | Inherits most restrictive member policy | Not allowed (405) |
---
## Auth Policies
### `Anonymous`
No credentials required. NGINX serves requests without `auth_basic`.
### `Authenticated`
HTTP Basic Auth enforced via an NGINX `auth_basic` directive backed by an htpasswd file.
Multiple users are supported — the operator compiles one htpasswd file from all referenced
credential Secrets and volume-mounts it into the NGINX pod.
---
## Multi-User Model
Auth policies reference a list of `secretRefs`, not a single one.
Each entry points to a Kubernetes Secret that holds exactly one user's credentials.
The operator reads all referenced Secrets, hashes the passwords, and writes a **combined**
htpasswd file into the operator-managed `<name>-htpasswd` Secret.
```yaml
auth:
  download:
    policy: Anonymous
  upload:
    policy: Authenticated
    secretRefs:
      - deployer-credentials      # deployer account
      - ci-bot-credentials        # CI pipeline account
      - admin-credentials         # admin account
```
This means:
- Adding a user → add a new Secret and append its name to `secretRefs`.
- Removing a user → remove the `secretRef` entry (and optionally delete the Secret).
- Rotating a password → update the Secret; operator detects the change and rebuilds the htpasswd.
---
## Credential Secret Format (per user)
Each Secret holds **one** user's credentials:
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: deployer-credentials
  namespace: maven
  labels:
    maven.operator.io/credential: "true"   # allows the operator to watch it
type: Opaque
stringData:
  username: deployer
  password: s3cr3t
```
The operator **does not** require a pre-computed htpasswd entry. It generates the bcrypt/APR1
hash itself via `HtpasswdService` and stores the result in the managed `<name>-htpasswd` Secret.
> ⚠️ Usernames must be unique across all `secretRefs` within the same policy. The admission
> webhook validates this at apply time and rejects the CRD if a duplicate username is detected.
---
## Operator-Managed htpasswd Secret
The operator maintains a single `<name>-htpasswd` Secret containing **all** users for that
repository, combined:
```
deployer:$2y$10$...bcrypt...
ci-bot:$2y$10$...bcrypt...
admin:$2y$10$...bcrypt...
```
This Secret is mounted read-only into the NGINX pod at `/etc/nginx/auth/.htpasswd`.
---
## Secret Watch & Reconcile Lifecycle
```
User creates/updates any credential Secret (label: maven.operator.io/credential=true)
       │
       ▼
Operator secondary watch fires → finds all MavenRepositories referencing this Secret
       │
       ▼
For each affected MavenRepository:
  - Read all secretRefs in auth.download.secretRefs + auth.upload.secretRefs
  - HtpasswdService.BuildHtpasswdAsync(allSecrets) → combined htpasswd string
  - Patch <name>-htpasswd Secret
       │
       ▼
NGINX detects mounted Secret change (inotify / periodic reload every 30s) and reloads
```
---
## Separate Download and Upload Users
Download and upload policies each have their own independent `secretRefs` list.
The operator generates **two separate htpasswd files**:
| File | Policy | NGINX directive |
|------|--------|----------------|
| `/etc/nginx/auth/download.htpasswd` | `auth.download` | Used in the outer `location` block |
| `/etc/nginx/auth/upload.htpasswd` | `auth.upload` | Used in the `limit_except` block |
A user can appear in both (e.g. an admin who can both download and upload), or only one.
NGINX config pattern:
```nginx
location /repository/my-releases/ {
    # Download auth (outer block)
    auth_basic "Maven - my-releases";
    auth_basic_user_file /etc/nginx/auth/download.htpasswd;
    dav_methods PUT DELETE MKCOL COPY MOVE;
    limit_except GET HEAD OPTIONS {
        # Upload auth (replaces the outer auth for write methods)
        auth_basic "Maven Upload - my-releases";
        auth_basic_user_file /etc/nginx/auth/upload.htpasswd;
    }
}
```
When `download.policy == Anonymous`, the outer `auth_basic` block is omitted entirely, and
only the `limit_except` block carries auth.
---
## Child Resources for Auth
| Resource | Kind | Content |
|----------|------|---------|
| `<name>-download-htpasswd` | Secret | Combined htpasswd for download users |
| `<name>-upload-htpasswd` | Secret | Combined htpasswd for upload users |
Both are operator-managed and rebuilt whenever any referenced credential Secret changes.
---
## Upstream Auth (Proxy repos)
When a Proxy repo references an upstream with auth:
- A **single** credential Secret is referenced (`upstream.auth.secretRef`).
- The operator reads `username` + `password`, base64-encodes them.
- The `Authorization: Basic <base64>` header is injected into the NGINX ConfigMap.
- Only one upstream credential is ever needed (proxy-to-upstream is server-to-server).
> ⚠️ The ConfigMap containing upstream auth headers should be protected by RBAC. A future
> enhancement will use sealed secrets or an external secret operator (e.g. External Secrets Operator).
---
## Future Enhancements (post-MVP)
| Feature | Notes |
|---------|-------|
| Role-based auth (`reader` / `deployer` / `admin`) | Map roles to download/upload policies |
| LDAP / OIDC | Delegate auth to an identity provider |
| Token-based auth | Short-lived tokens instead of long-lived passwords |
| Per-artifact-path ACLs | Fine-grained access control within a repo |
