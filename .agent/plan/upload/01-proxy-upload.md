# 01 — Proxy Repository Upload Forwarding

## Concept

A Proxy repository normally caches reads from an upstream Maven server. With upload forwarding,
it can also proxy PUT/DELETE requests to the same upstream using fixed credentials stored in a
Kubernetes Secret.

This is designed for **internal-only** proxies (no Gateway/Ingress) that act as migration bridges:
clients deploy to the Proxy URL, and the Proxy forwards to the real upstream (e.g., Nexus).

## Authentication Model

### Client → Proxy
Two modes are supported:

| Mode | Behavior | Use Case |
|------|----------|----------|
| `Passthrough` | Enforce the proxy's existing upload auth policy; client provides credentials. | Standard secured deployment. |
| `Override` | Ignore any configured upload auth; accept uploads from anyone (or with anonymous access). | Migration scenario where legacy clients can't be updated to use new credentials. |

### Proxy → Upstream
Always uses fixed credentials from a single Secret:

```yaml
upload:
  enabled: true
  mode: Passthrough          # or Override
  upstreamCredentialsRef:
    name: nexus-upload-creds
    namespace: maven         # optional, defaults to same namespace
```

Secret format (same as existing credential secrets):
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: nexus-upload-creds
type: Opaque
stringData:
  username: deployer
  password: s3cr3t
```

## Security Constraint — Internal Only

Upload forwarding is **only allowed** when the Proxy has no externally facing endpoints.
The operator must validate this at admission time (webhook) and/or reconcile time.

### Detection Rules

A Proxy is considered "externally exposed" if:

1. A `Gateway` or `Ingress` resource exists that routes traffic to this Proxy's Service, OR
2. The Proxy's Service has `type: LoadBalancer` or `type: NodePort`, OR
3. An annotation on the MavenRepository explicitly marks it as external:
   `maven.operator.io/externally-exposed: "true"`

If any rule matches and `upload.enabled: true`, the operator:
- Sets `status.phase: Invalid`
- Adds a condition: `{type: UploadForbidden, reason: ExternallyExposedProxy}`
- Emits a Warning event explaining why uploads are blocked

### Override (Advanced)

A cluster admin can explicitly allow uploads on an exposed proxy by setting:
```yaml
upload:
  enabled: true
  forceAllowOnExternal: true   # requires explicit acknowledgment of risk
```

This is intentionally opt-in and should be rare. It allows scenarios where the proxy sits behind
a corporate firewall with its own network-level access controls.

## NGINX Configuration Changes

### Current (read-only)
```nginx
proxy_cache_methods GET HEAD;
# No WebDAV, no PUT/DELETE handling
```

### With Upload Forwarding
```nginx
location /repository/my-proxy/ {
    # Download auth (existing behavior)
    {{ if download_policy == "Authenticated" }}
    auth_basic "Maven - my-proxy";
    auth_basic_user_file /etc/nginx/auth/download.htpasswd;
    {{ end }}

    proxy_pass http://upstream-maven-central/;
    proxy_cache maven_cache;
    proxy_cache_methods GET HEAD;

    # Upload forwarding for write methods
    if ($request_method ~ ^(PUT|DELETE|MKCOL)$) {
        # Override auth if in "Override" mode (skip client auth)
        {{ if upload_mode == "Passthrough" }}
        limit_except GET HEAD OPTIONS {
            {{ if upload_policy == "Authenticated" }}
            auth_basic "Maven Upload - my-proxy";
            auth_basic_user_file /etc/nginx/auth/upload.htpasswd;
            {{ end }}

            # Inject upstream credentials
            proxy_set_header Authorization "Basic $upstream_auth_base64";

            # Forward the write request (no caching)
            proxy_cache off;
            proxy_pass http://upstream-maven-central/;
        }
        {{ else if upload_mode == "Override" }}
        # No client auth required — accept and forward directly
        set $is_write 1;
    }

    if ($is_write) {
        proxy_cache off;
        proxy_set_header Authorization "Basic $upstream_auth_base64";
        proxy_pass http://upstream-maven-central/;
    }
        {{ end }}
    }
}
```

Key behaviors:
- GET/HEAD requests are cached as before.
- PUT/DELETE/MKCOL bypass the cache and forward to upstream with fixed credentials.
- Client auth is enforced (Passthrough) or skipped (Override) based on mode.

## Request Flow

```
Client (mvn deploy)
    │
    ▼  PUT /repository/my-proxy/com/example/artifact/1.0/artifact-1.0.jar
Proxy NGINX
    │
    ├─ [Passthrough] Validate client credentials against upload.htpasswd
    │   └─ Fail → 401 Unauthorized
    │
    ├─ [Override] Skip client auth entirely
    │
    ├─ Read upstream credentials from Secret (rendered into NGINX config)
    │
    ▼  PUT /com/example/artifact/1.0/artifact-1.0.jar
        Authorization: Basic <upstream-creds>
Upstream Maven Server (Nexus/Artifactory/Central)
    │
    ▼  201 Created or 4xx error
Proxy NGINX → returns upstream response to client
```

## Error Handling

| Upstream Response | Proxy Behavior |
|-------------------|----------------|
| 200, 201 | Return as-is to client (upload succeeded) |
| 401, 403 | Return 502 Bad Gateway with message: "Upstream rejected upload — check proxy credentials." |
| 404 | Return 404 — path not found on upstream |
| 5xx | Return as-is (upstream transient error) |

The proxy never caches write responses.

## Status Reporting

New status fields for Proxy repos with uploads:

```yaml
status:
  upload:
    enabled: true
    mode: Passthrough
    upstreamCredentialsConfigured: true   # false if Secret is missing/unreadable
    lastSyncError: null                   # or error message if credentials can't be loaded
```

## Migration Use Case Example

A team migrates from Nexus to the operator-managed Maven setup:

1. Create a Proxy repo pointing at Nexus as upstream.
2. Enable upload forwarding with `mode: Override` (legacy CI still uses old Nexus creds).
3. Configure `upload.upstreamCredentialsRef` with Nexus deployer credentials.
4. Point all `mvn deploy` settings.xml to the new Proxy URL.
5. Gradually migrate artifacts to Hosted repos; eventually switch Proxy to read-only.

This allows zero-downtime migration without touching every CI pipeline's credentials.
