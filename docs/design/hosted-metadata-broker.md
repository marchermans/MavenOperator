# Design: metadata broker sidecar for hosted repositories

**Status:** final draft, ready for implementation · **Date:** 2026-10-08 · **Scope:** `type: Hosted` only (not Proxy, not Virtual)
**Affected code:** new project `MavenOperator.MetadataBroker`; `MavenOperator/Templates/nginx-hosted.conf.scriban`; `MavenOperator/Reconcilers/HostedRepositoryReconciler.cs`

> Ground-truth facts used below: hosted repos run a single-replica Deployment
> (`replicas: 1`) with an RWO PVC (DeletionPolicy=Retain) mounted at
> `/var/maven/repository`; NGINX serves it statically and writes uploads via the
> `dav` module. Default URL prefix is `/repository/{name}`
> (`RepositoryPathHelper.ResolvePathPrefix`).

## 1. Problem — TOCTOU race on `maven-metadata.xml`

When two builds of the same coordinates deploy concurrently (mvn `deploy` or
Gradle `publish`), each client generates `maven-metadata.xml` from its **local**
view and PUTs it, blindly overwriting whatever is on disk:

```
t0  build A reads metadata   → {1.0}
t0  build B reads metadata   → {1.0}        (both see unmodified file)
t1  A uploads jar/pom 2.0 + metadata {1.0, 2.0}
t2  B uploads jar/pom 3.0 + metadata {1.0, 3.0}   ← version 2.0 is lost
```

The final file lists whichever build finished last; the other version's
artifacts sit on disk **unlisted**. Clients then fail to resolve them until the
next deploy happens to re-merge — non-deterministic and long-lived, because
Maven/Gradle cache metadata per `updatePolicy` (daily by default).

The existing nginx template already documents the client behavior that makes
this race window exist (`nginx-hosted.conf.scriban`, near
`open_file_cache_errors off`): *"Maven clients (Gradle, mvn) issue a GET to
check for an existing maven-metadata.xml immediately before PUTting a new one."*

Today's data plane cannot fix this: hosted repos serve the PVC statically and
write uploads through nginx `dav` (`alias /var/maven/repository/`,
`dav_methods PUT DELETE MKCOL COPY MOVE`) — i.e. metadata files land on disk
verbatim, unmodified.

## 2. Approach

Introduce a **metadata broker sidecar** in the hosted pod. NGINX routes every
request whose path ends in `maven-metadata.xml` to it (all methods). The broker:

- treats **disk as the single source of truth**, never client-supplied XML;
- answers **GET/HEAD** with metadata generated on demand from the version
  directories actually present on disk;
- treats **PUT/POST** as an *invalidation + ack*: drains and discards the body,
  forces regeneration, returns 2xx. It does not write client bytes to disk —
  it writes back its own canonical rendering (debounced, atomic rename).

Because both Maven deploy and Gradle publish upload the version's jar/pom/etc.
**before** the metadata file, the new version directory always exists on disk
by the time the metadata PUT arrives — so the regenerated XML already contains
the uploader's own version, plus everyone else's. The race is eliminated
structurally: two concurrent PUTs are both acks of the *same* regeneration, in
whatever order they arrive.

### Why a sidecar and not something else (alternatives considered)

| Alternative | Verdict |
|---|---|
| nginx `dav` + post-write hook / Lua script to rebuild XML | Works, but no Lua precedent in the stack; hard to unit-test; regexes for snapshot parsing get ugly. C# is already our data-plane language. |
| Delayed/queued uploads with server-side merge | Adds latency and complexity to every deploy; still needs a generator. |
| Client-side retries / last-write-wins | Cannot fix: the losing client has no knowledge of the other version. |

Precedent in this codebase for "metadata is derived content": **Virtual mode**
already runs `MavenOperator.VirtualProxy` with a `MetadataMergeService` that
merges member metadata and caches it 60 s in-process. The broker generalizes
that principle to hosted repos, where the source of truth is one PVC instead
of N upstream URLs.

## 3. Architecture

```
                         ┌──────────────────────────── hosted pod ───────────────────────────┐
 mvn/gradle ──80──▶ nginx │  location ~ .*maven-metadata\.xml$                                │
        (auth as today)   │      proxy_pass http://127.0.0.1:8091                             │
                          │      error_page 502/503/504 = @meta_fallback_read ─▶ static from PVC│
                          │  location <prefix> (dav, everything else)                        │
                          │                                    │   ▲                            │
                          │                    /var/maven/repository (PVC, shared volume)      │
                          │                       │writes(dav)  ▲rename                      │
                          │                 metadata-broker :8091 (binds loopback only)       │
                          └──────────────────────────────────────────────────────────────────┘
```

- **New project** `MavenOperator.MetadataBroker`: ASP.NET Core Minimal API,
  no KubeOps/EF dependencies — storage access + XML generation. ~350–450 LOC.
  Image built alongside VirtualProxy; reconciler reads it from a
  `METADATA_BROKER_IMAGE` env var (same pattern as `VIRTUAL_PROXY_IMAGE`).
- **Pod changes** (`HostedRepositoryReconciler`): one extra container, port
  8091, loopback-only (no Service port), same repository volume mount.
  Readiness probe on its `/healthz`. Replicas stay 1 (RWO PVC — the
  single-writer invariant is documented).
- **NGINX changes** (`nginx-hosted.conf.scriban`): one new regex location ahead
  of the generic `location {{ location_path_prefix }}`, plus a static fallback.

### NGINX sketch

```nginx
# disk path for the fallback (prefix is known at render time)
map $uri $meta_disk_path_{{ var_name }} {
    ~^{{ path_prefix_regex }}(?P<p>.*)   "/var/maven/repository/$p";
}

location ~ ^{{ path_prefix_regex }}.*maven-metadata\.xml$ {
    # same auth directives as the generic location (auth_request / limit_except)

    proxy_pass http://127.0.0.1:8091;
    proxy_set_header X-Original-Method $request_method;   # read vs write, as AuthProxy does
    proxy_intercept_errors on;
    error_page 502 503 504 = @meta_fallback_read;         # broker down → degrade

    add_header Cache-Control "no-cache, must-revalidate" always;
}

location @meta_fallback_read {                            # graceful degradation
    if ($request_method ~ ^(PUT|POST)$) {
        return 409;                                      # D7: no dav write-fallback
    }
    add_header Retry-After 5 always;
    try_files $meta_disk_path_{{ var_name }} =404;
    add_header Cache-Control "no-cache, must-revalidate" always;
}
```

**Read fallback** (`GET`/`HEAD`) — `proxy_intercept_errors on;` plus
`error_page 502 503 504 = @meta_fallback_read;`, where the named location does
`try_files $meta_disk_path_{{ var_name }} =404;`. If the broker is down, reads
degrade to today's static behavior.

**Write fallback (deliberate non-fallback)** — if the broker is down when a
metadata PUT arrives, NGINX returns **409** with `Retry-After: 5` and logs an
error; it does *not* fall back to a raw `dav` write. Rationale: a raw dav write
of client-supplied metadata would silently re-introduce the exact TOCTOU race
this feature removes — a failed-but-retried upload is strictly preferable to a
corrupt listing. Broker downtime is expected to be seconds (liveness probe +
pod restart policy), and Maven/Gradle clients retry uploads.

Other notes:

- The regex matches **both** metadata levels (§5) in one location.
- Auth is unchanged: the outer `auth_request /auth/validate` (or basic-auth
  `limit_except`) applies exactly as it does for other paths, *before* the
  request reaches the broker. The broker sees only authenticated traffic on
  loopback.
- No Service port is added; the broker binds loopback inside the pod.

## 4. Broker request handling

### GET / HEAD
1. Classify the path (§5) → artifact directory to scan.
2. **Fingerprint**: for listing metadata — the set of subdirectory names under
   `<g>/<a>` plus each dir's max mtime; for snapshot metadata — timestamped
   file entries in the version dir (name + mtime). One `readdir` per request,
   O(versions) — cheap even with thousands of versions.
3. Fingerprint unchanged → serve cached bytes (200/304 via ETag = SHA-256 of
   body; `Last-Modified` aligned with the XML's `<lastUpdated>`).
4. Changed (or first sight) → **single-flight** regeneration under a per-path
   `SemaphoreSlim`: serialize XML, update cache, schedule debounced write-back
   (~1–2 s), respond 200 + `X-Maven-Metadata-Source: generated`.
5. No version directories present → fresh 404 (never cached; mirrors the
   existing `open_file_cache_errors off` rationale).

### PUT / POST
- Drain and discard the request body (nginx's `client_max_body_size` still caps it).
- Do **not** persist client bytes for this path. Invalidate the artifact's cache
  entry, schedule debounced regeneration + write-back, return **201** with
  `X-Maven-Metadata-Accepted: true`.
- The client's post-upload verification GET (the one documented in the template
  comment) now receives regenerated XML containing its own version.

### DELETE
Drain body (if any), invalidate, 204. Write-back removes stale canonical file if
empty listing results from it; a plain delete is also fine — next regeneration
recreates as needed.

### Write-back (canonicalization of the PVC)
Debounced per artifact path: render to `maven-metadata.xml.tmp-<rand>` then
`rename()` over the target — atomic on POSIX, no torn reads for any direct PVC
consumer (import tooling, dashboard file listing, future multi-writer setups).
Failure → keep serving from memory, log + retry with backoff; repeated failures
raise a `Warning` event / status condition (`MetadataBrokerDegraded`) so the
dashboard surfaces it.

### Concurrency & consistency argument — race walkthrough

```
time   build A                                        build B
s1     GET metadata → broker: {1.0}                  (both see unmodified file)
s2     PUT 2.0.jar/pom ─▶ dav writes to PVC
s3                                          PUT 3.0.jar/pom ─▶ dav writes to PVC
                                             (jars on disk now: 1.0, 2.0, 3.0)
       PUT maven-metadata.xml → broker: drain body,
s           invalidate cache, ack 201             PUT maven-metadata.xml → same: 201
s4     GET metadata → fingerprint changed →
             single-flight regenerate from disk = {1.0, 2.0, 3.0}   ✔ both listed;
             B's next GET returns the identical bytes (cache hit)
s5     debounced write-back (~2 s) atomically replaces
           maven-metadata.xml on PVC with canonical XML
```

- Two concurrent builds: jars/poms are written by nginx `dav` (untouched path);
  both metadata PUTs are ack-only invalidations; the first GET after either PUT
  regenerates from a directory listing that contains **both** version dirs,
  regardless of order. ✔ race gone.
- If a client's jar lands *after* its own verification GET (violating the
  documented ordering assumption), the listing is briefly short by one version;
  the fingerprint check self-heals on the next read — eventual consistency in
  seconds, versus "until some future deploy overwrites" today. ✔ strictly better.
- All writers converge to the same canonical rendering because generation is a
  pure function of disk state (deterministic ordering; `lastUpdated` = max mtime).

## 5. The two metadata shapes (both in scope)

Maven writes metadata at **two** levels, and both race identically:

1. **Version listing** — `<g>/<a>/maven-metadata.xml`. Generated from the
   subdirectories of `<g>/<a>` that contain at least one recognized artifact
   file (`*.jar *.war *.ear *.aar *.pom`):

```xml
<metadata modelVersion="1.1.0">
  <groupId>…</groupId><artifactId>…</artifactId>
  <versioning>
    <release>HIGHEST_NON_SNAPSHOT</release>     <!-- omitted if none -->
    <latest>HIGHEST_OVERALL</latest>            <!-- optional, include when != release -->
    <versions> …sorted ascending (numeric-aware)… </versions>
    <lastUpdated>yyyyMMddHHmmss</lastUpdated>   <!-- = max mtime across dirs/files -->
  </versioning>
</metadata>
```

   `release`/`latest` matter: clients resolve version ranges and the
   `RELEASE`/`LATEST` keywords from them. Computing is cheap; include it.

2. **Snapshot metadata** — `<g>/<a>/<v>-SNAPSHOT/maven-metadata.xml`. Generated
   by parsing timestamped files matching
   `^(.*)-(\d{8}\.\d{6}-\d+)\.(ext)$` in the version dir:

```xml
<metadata modelVersion="1.1.0">
  <versioning>
    <snapshot><localCopy>false</localCopy><value>v-SNAPSHOT</value><updated>…</updated></snapshot>
    <lastUpdated>…</lastUpdated>
    <snapshotVersions>
      <snapshotVersion><extension>pom</extension><value>v-20261008.123456-1</value><updated>…</updated></snapshotVersion>
      …one per extension…
    </snapshotVersions>
  </versioning>
</metadata>
```

   Snapshot deploys are the *most* race-prone case (CI re-deploys the same
   version constantly), so this shape is not optional.

Out of scope: repo-root `maven-metadata.xml` / `<pluginGroups>` files (static,
not produced by deploy); per-file checksums (still client-uploaded as today).

## 6. What this deliberately does NOT fix

- **Same-version conflicting file contents** (two builds publish different
  bytes for the identical coordinate): last-write-wins on the *files* is
  inherent to any single-path store; only metadata listing is mergeable and
  that's what we're fixing.
- Proxies: upstream owns its metadata; proxy caching already revalidates it.
- Virtual: already merges member metadata in `MetadataMergeService`; if a
  virtual target is a hosted repo, the broker's GET flows through nginx
  transparently — no change needed.

## 7. Rollout & failure modes

| Scenario | Behavior |
|---|---|
| Broker image missing / not yet built (first deploys after this ships) | `error_page` fallback → exactly today's static behavior. Strictly additive. |
| Broker crash/restart | Memory cache lost; disk is source of truth → regenerate on first hit; fallback serves last canonical file meanwhile. PVC survives pod restarts → zero data loss. |
| Write-back IO failure (PVC full) | Serve from memory, retry w/ backoff, `MetadataBrokerDegraded` condition + event. Reads stay correct. |
| Import job writes version dirs directly to PVC (bypassing nginx) | No PUT signal arrives — but the fingerprint check catches new/changed directories on the next GET. Self-healing by construction. |

Optional spec gate: `spec.hosted.metadataBroker.enabled` (default `true`) for a
per-repo escape hatch if needed; not required for rollout given the fallback.

## 8. Decision record

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Disk is the single source of truth; client metadata bodies are drained and discarded | The stale client bytes *are* the bug. Any design that persists them keeps a race window. |
| D2 | PUT = invalidate + 201 ack, not write-then-recalculate | Makes concurrent PUTs order-independent (both are no-op signals to the same regeneration). |
| D3 | Fingerprint-per-GET instead of an invalidation bus | Catches *any* writer (nginx dav, import job on PVC, future tools) with one `readdir`; no IPC needed. |
| D4 | Generate both metadata levels (version listing + snapshot `snapshotVersions`) | Snapshot deploys are the most race-prone case; a half-fix would leave CI pipelines broken. |
| D5 | C# Minimal API sidecar, not nginx Lua | Matches existing data-plane stack (`VirtualProxy`, `AuthProxy`); unit-testable XML generation; no Lua precedent or tooling here. |
| D6 | Debounced atomic write-back (temp + rename) | Keeps the PVC canonical for direct readers and the static fallback; rename is atomic on POSIX. |
| D7 | 409 + Retry-After on metadata PUT while broker down (no dav fallback) | A raw dav fallback would silently re-introduce TOCTOU; clients retry uploads, downtime is seconds. |
| D8 | `lastUpdated` = max mtime of the artifact directory (not generation time) | Stable across regenerations → no 304 churn, and it advances exactly when content changes — matching Maven `updatePolicy` semantics. |
| D9 | Loopback-only binding, no Service port | Broker is pod-internal; only nginx may reach it (same pattern as AuthProxy :8080).

## 9. Test plan

**Unit (`MavenOperator.Tests.Unit.MetadataBroker`)**
- XML generation: mixed release+snapshot versions, ordering (numeric-aware),
  `<release>`/`<latest>` computation, `lastUpdated` stability across identical
  regenerations; snapshot file parsing (multiple extensions/build numbers).
- Fingerprint invalidation (new dir, changed mtime, deleted dir).
- PUT semantics: body drained, no client bytes persisted, cache invalidated, 201.
- ETag/304 correctness; deterministic output (byte-stable across regenerations).

**Integration / E2E (k3d, `maven-operator-test`)**
- Race simulation: parallel upload of version A and B dirs + their metadata PUTs
  → single GET asserts **both** versions listed (the exact failure mode of §1).
- Import-job path: create a version dir directly on the PVC, no PUT → next GET
  lists it.
- Fallback: stop the broker container → GET still serves last canonical file
  (static); metadata PUT returns **409** with `Retry-After` and is *not*
  written via `dav` (D7); artifact PUTs are unaffected.
- Regression: proxy & virtual suites unchanged.

## 10. Effort estimate

| Piece | Size |
|---|---|
| `MavenOperator.MetadataBroker` project + generation service + tests | ~350–450 LOC |
| `nginx-hosted.conf.scriban`: location, map, fallback | ~25 lines |
| `HostedRepositoryReconciler`: container/volume/probe/image env | ~60 LOC |
| E2E race test + fixture helpers | ~150 LOC |

Roughly one working day including the E2E run. No CRD schema changes; no
migrations (emptyDir/PVC layout unchanged); strictly additive rollout.
