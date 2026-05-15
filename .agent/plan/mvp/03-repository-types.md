# 03 — Repository Types
## Hosted Repository
A `Hosted` repository stores Maven artifacts directly on a PersistentVolume, served by NGINX with the WebDAV module.
### Lifecycle
1. Operator creates a PVC (`<name>-pvc`).
2. Operator renders an NGINX config that:
   - Serves GET/HEAD on the artifact path (optionally with `auth_basic` for download).
   - Restricts PUT/DELETE to authenticated users only (upload auth).
   - Uses `autoindex on` to support Maven metadata browsing.
3. NGINX mounts the PVC at `/var/maven/repository`.
### NGINX modules required
- `ngx_http_dav_module` (WebDAV PUT/DELETE)
- `ngx_http_auth_basic_module` (HTTP Basic Auth)
### Snapshot vs Release
- No distinction at Operator level — path-based separation is convention.
- Operators may later add a `snapshotPolicy` field to reject or allow redeploy.
---
## Proxy Repository
A `Proxy` repository acts as a caching proxy for a remote Maven repository (e.g., Maven Central).
### Lifecycle
1. Operator renders an NGINX config with `proxy_pass` pointing to the upstream URL.
2. NGINX caches responses in a local `proxy_cache` directory (emptyDir or PVC).
3. Optional upstream auth is injected as a `Proxy-Authorization` header via Kubernetes Secret.
4. Download auth policy applies to the local endpoint as well.
### Cache Invalidation
- Cache TTL is configurable via `upstream.cacheTtl` (default: `1d`).
- Artifacts are immutable by convention in Maven release repos — snapshot repos use short TTLs.
---
## Virtual Repository
A `Virtual` repository fans out requests to multiple member repositories in priority order.
### Request Flow (download)
```
Client GET /repository/group/com/example/foo/1.0/foo-1.0.jar
  │
  ▼
C# AggregationProxy (tries members in order)
  ├─ GET from member[0] → 200 OK → return to client
  ├─ GET from member[0] → 404   → try member[1]
  ├─ GET from member[1] → 200 OK → return to client
  └─ All 404 → return 404 to client
```
### maven-metadata.xml Merging
See document 05 for full details. In brief:
- The proxy fetches `maven-metadata.xml` from **all** members in parallel.
- It merges them using the rules in document 05.
- The merged result is cached in-process with a short TTL (e.g., 60s).
### Upload to Virtual Repositories
Maven best practice: **upload directly to a Hosted member**, not via the Virtual group.
The operator follows this convention — upload (PUT) requests to a Virtual repository return `405 Method Not Allowed` with a helpful error body pointing to the target Hosted repo.
