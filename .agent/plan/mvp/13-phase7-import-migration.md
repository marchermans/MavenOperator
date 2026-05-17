# 13 — Phase 7: Import & Migration

## Goal

Enable users to migrate existing Maven repository servers into the MavenOperator
platform with zero re-publishing. Phase 7 defines **three distinct transfer modes**
that cover different source types and infrastructure constraints:

| Mode | Source | Transfer mechanism |
|------|--------|--------------------|
| **API crawl** | Reposilite (REST) or JFrog Artifactory Cloud | HTTP download → direct PVC write |
| **PVC snapshot clone** | Any Reposilite-compatible on-disk layout (backup/external PVC) | Raw file copy from a mounted external PVC |
| **Live PVC clone** | Running Reposilite with a shared RWX PVC | Raw file copy from the live Reposilite PVC (mounted concurrently or after scale-down) |

All three modes share the same `MavenRepositoryImport` CRD and the same
`MavenOperator.ImportJob` console binary. The operator selects the correct
strategy at Job-launch time based on `spec.source.type` and whether a PVC ref
is provided.

> **RWX storage is now a baseline requirement for Hosted repositories.**
> In API-crawl mode the Job writes directly to the repository's backing PVC —
> no HTTP round-trip, no NGINX auth overhead. This is also the correct
> foundation for future HA deployments where multiple NGINX pods share the same
> PVC via `ReadWriteMany`.

---

## 1  Storage Model Change: RWX as Default

### 1.1  Motivation

Original design: import Job downloads artifacts and PUTs them to NGINX via HTTP.
Problems:
- Unnecessary double-copy (RAM buffer → HTTP → NGINX → disk).
- NGINX auth policy adds latency to every PUT.
- Bandwidth consumed twice on the node network stack.
- Does not scale to HA — multiple NGINX replicas require a shared PVC anyway.

### 1.2  New Requirement

`spec.storage.accessMode` defaults to `ReadWriteMany`.
The `StorageClass` must support RWX; the operator emits a `Warning` event if
the chosen `StorageClass` is RWO-only.

**Backward-compatibility**: existing Hosted repos with a `ReadWriteOnce` PVC
continue to work normally. Import Jobs with such repos automatically fall back
to HTTP PUT via `HttpSink` and emit a `Warning` condition on the import CR.

### 1.3  CRD Schema Change (`MavenRepositorySpec`)

```yaml
spec:
  storage:
    size: 10Gi
    storageClassName: ""
    accessMode: ReadWriteMany   # NEW — was implicitly ReadWriteOnce
    deletionPolicy: Retain
```

CEL validation: `accessMode` must be one of `ReadWriteOnce`, `ReadWriteMany`,
`ReadWriteOncePod`.

---

## 2  `MavenRepositoryImport` CRD

```yaml
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepositoryImport
metadata:
  name: my-import
spec:
  # --- Target ---
  targetRepository: my-hosted-repo   # Hosted MavenRepository in same namespace

  # --- Source (exactly one sub-field) ---
  source:
    # Option A: REST API crawl (Reposilite or JFrog Cloud)
    api:
      type: reposilite | jfrog-cloud
      url: https://repo.example.com
      repository: releases
      credentialsSecret: my-src-creds   # keys: username+password OR token

    # Option B: External / snapshot PVC (no live server needed)
    pvcSnapshot:
      claimName: reposilite-backup-pvc
      subPath: ""                        # optional sub-directory within the PVC
      reposiliteLayout: true             # false = raw Maven layout

    # Option C: Live Reposilite PVC (RWX-capable StorageClass required on source)
    pvcLive:
      claimName: reposilite-data-pvc
      reposiliteDeployment: reposilite  # Deployment to scale down before mount
      scaleDownDuration: 60s            # 0s = no scale-down (requires RWX)
      subPath: ""

  # --- Common filters ---
  filters:
    includeGroups: []          # glob list, e.g. ["com.example.*"]
    excludeGroups: []
    includeVersions: []        # glob, e.g. ["1.*", "2.0.*"]
    sinceTimestamp: ""         # RFC3339

  # --- Options ---
  options:
    parallelism: 8
    checksumValidation: true
    dryRun: false
    overwriteExisting: false
    # auto = directWrite when RWX PVC available, else http
    transferMode: auto | directWrite | http

status:
  phase: Pending | Running | Succeeded | Failed | PartiallyFailed
  transferMode: directWrite | http      # resolved at runtime
  artifactsDiscovered: 0
  artifactsCopied: 0
  artifactsFailed: 0
  bytesTransferred: 0
  startTime: ""
  completionTime: ""
  conditions: []
  failedArtifacts: []    # up to 100 entries for debugging
```

CEL admission rules:
- Exactly one of `source.api`, `source.pvcSnapshot`, `source.pvcLive` must be set.
- `pvcLive.scaleDownDuration` must parse as a Go duration string.

---

## 3  Import Modes in Detail

### 3.1  Mode A — API Crawl + Direct PVC Write

The Job crawls the source via REST API and writes artifact bytes directly to
the mounted target Hosted repository PVC:

```
Job Pod volumes:
  - name: repo-data
    persistentVolumeClaim:
      claimName: <targetRepository>-pvc
      readOnly: false
```

`DirectPvcSink` writes to `/data/<group-path>/<artifactId>/<version>/<filename>` —
the same Maven layout NGINX already serves. No HTTP, no auth overhead.

**Fallback to `HttpSink`**: if the Hosted PVC is `ReadWriteOnce` and already
claimed by NGINX, the operator falls back to HTTP PUT and emits a `Warning`
condition on the import CR.

### 3.2  Mode B — Snapshot / External PVC Clone

Used when the user has a backup volume or a decommissioned Reposilite PVC:

1. Job mounts **both** source PVC (read-only) and target PVC (read-write).
2. `PvcSnapshotSource` walks the source filesystem. If `reposiliteLayout: true`,
   strips the leading `/<repository>/` path segment to obtain Maven-standard paths.
3. `DirectPvcSink` writes files to target PVC.
4. `maven-metadata.xml` files are skipped — operator regenerates them.
5. Filters (`includeGroups`, `sinceTimestamp`) applied via file-path + `mtime`.

**Source PVC constraint**: if the source PVC is RWO and bound to a running pod,
the operator sets `status.phase = Failed` with an `Error` condition and does not
create the Job.

### 3.3  Mode C — Live Reposilite PVC Clone

Used when Reposilite runs in the same cluster and the PVC supports concurrency:

1. Operator records original Deployment replicas in annotation
   `maven.operator.io/pre-import-replicas`.
2. If `scaleDownDuration > 0s`: scales Reposilite Deployment to 0; waits for
   all pods to terminate (up to `scaleDownDuration`).
3. Creates import Job mounting live PVC (read-only) + target PVC (read-write).
4. After Job completes: operator restores Reposilite to original replica count.
5. If `scaleDownDuration: 0s`: PVC must be RWX; Job runs concurrently; a
   `Warning` condition notes possible read inconsistency.

**Finalizer** `maven.operator.io/import-cleanup`: ensures Reposilite is always
scaled back up, even if the CR is deleted mid-run.

---

## 4  `MavenOperator.ImportJob` Project Layout

```
MavenOperator.ImportJob/
  MavenOperator.ImportJob.csproj
  Program.cs
  Sources/
    IRepositorySource.cs          # IAsyncEnumerable<ArtifactDescriptor> CrawlAsync()
    ReposiliteApiSource.cs        # Mode A: HTTP directory walk
    JFrogCloudApiSource.cs        # Mode A: Artifactory flat-list API
    PvcSnapshotSource.cs          # Modes B+C: filesystem walk on mounted PVC
  Sinks/
    IRepositorySink.cs            # Task WriteAsync(ArtifactDescriptor, Stream)
    DirectPvcSink.cs              # Direct filesystem write to target PVC
    HttpSink.cs                   # HTTP PUT fallback (WebDAV)
  Services/
    ArtifactCrawler.cs            # Source → Sink with bounded parallelism
    ChecksumValidator.cs          # SHA-256/SHA-1 comparison
    MavenLayoutTranslator.cs      # Reposilite path → Maven standard path
    ProgressReporter.cs           # Live progress patches to parent CR
    PvcAccessChecker.cs           # Detects RWO conflicts before Job launch
  Models/
    ArtifactDescriptor.cs
    ImportResult.cs
    TransferMode.cs               # Enum: DirectWrite, Http
```

Environment variables injected by the operator:

| Env var | Description |
|---------|-------------|
| `IMPORT_MODE` | `api-reposilite`, `api-jfrog`, `pvc-snapshot`, `pvc-live` |
| `IMPORT_TRANSFER_MODE` | `direct-write`, `http` |
| `SOURCE_URL` / `SOURCE_REPO` | API modes only |
| `SOURCE_PVC_MOUNT` | PVC modes only — mount path inside container |
| `TARGET_PVC_MOUNT` | Mount path of target repo PVC (`direct-write` mode) |
| `TARGET_HTTP_URL` | HTTP fallback mode only |
| `CREDENTIALS_FILE` | Path to mounted Secret JSON file |
| `IMPORT_OPTIONS_JSON` | JSON-encoded `ImportOptions` |

---

## 5  Source Adapters

### 5.1  `ReposiliteApiSource`

```
GET /api/maven/details/{repo}/{path}  →  { files: [{name, type, contentLength, lastModified}] }
GET /api/maven/repository/{repo}/{path} → artifact bytes
```

- Recursive BFS `CrawlAsync`; `sinceTimestamp` via `lastModified`.
- Skip `maven-metadata.xml`; always include checksum files.
- Polly retry: 3 attempts, exponential back-off.

### 5.2  `JFrogCloudApiSource`

```
GET /artifactory/api/storage/{repo}/?list&deep=1&listFolders=0
  →  { files: [{uri, lastModified, size, sha1, sha256}] }
GET /artifactory/{repo}{uri} → artifact bytes
```

- Bearer-token or HTTP Basic auth.
- Skip `.asc` signatures unless `options.includeSignatures: true`.
- `IHttpClientFactory` named client `jfrog`; Polly retry.

### 5.3  `PvcSnapshotSource`

- `Directory.EnumerateFiles(mountPath, "*", AllDirectories)`.
- `reposiliteLayout: true` → strip `/<repository>/` prefix.
- `sinceTimestamp` via `File.GetLastWriteTimeUtc`.
- Group glob filter applied on Maven group path.
- Returns `ArtifactDescriptor` with `FilePath` set (no download step).

---

## 6  Sink Adapters

### 6.1  `DirectPvcSink`

```csharp
var dest = Path.Combine(targetMount, artifact.RelativePath);
Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
await using var fs = File.OpenWrite(dest);
await sourceStream.CopyToAsync(fs);
```

`overwriteExisting: false` → skip if `File.Exists(dest)`.
For PVC-to-PVC modes, `sourceStream` is a `FileStream` from `artifact.FilePath` —
no network hop.

### 6.2  `HttpSink` (fallback)

HTTP PUT to NGINX WebDAV endpoint. Only used when `transferMode: http` or when
`DirectPvcSink` is unavailable (RWO conflict auto-detected).

---

## 7  `MavenLayoutTranslator`

Reposilite on-disk layout:

```
/<repository>/com/example/my-lib/1.0/my-lib-1.0.jar
```

Maven standard layout (target):

```
/com/example/my-lib/1.0/my-lib-1.0.jar
```

`Translate(path, repositoryName)`: strip leading `<repositoryName>/`; normalise
separators; remove `.index`/`.cache` directory entries.

---

## 8  Operator Controller Changes

### 8.1  `MavenRepositoryImportController`

**On Pending**:
1. Validate `targetRepository` exists and is `Ready`.
2. Resolve transfer mode (`auto` → probe PVC access mode).
3. Mode B: verify source PVC is not RWO-bound to a running pod.
4. Mode C: record replica count; optionally scale down Deployment.
5. Create Job with correct volumes and env vars.
6. Patch `status.phase = Running`, `status.transferMode`.

**Job volume mapping per mode**:

| Mode | Source volume | Target volume |
|------|---------------|---------------|
| A (api crawl, direct) | — | target repo PVC (RW) |
| A (api crawl, http fallback) | — | — |
| B (pvc snapshot) | source PVC (RO) | target repo PVC (RW) |
| C (pvc live) | Reposilite PVC (RO) | target repo PVC (RW) |

**On Running**: poll Job; sync progress from CR annotations.
**On complete**: patch `status.phase`; emit Event; restore Reposilite replicas (Mode C).
**Finalizer**: always restore Reposilite replicas on CR deletion.

### 8.2  RBAC additions for import Job ServiceAccount

- `get/patch` `Deployments` — Mode C scale-down/up.
- `get/patch` `MavenRepositoryImports` — progress updates.
- `get` `PersistentVolumeClaims` — RWO conflict check.

---

## 9  Performance Comparison — k6 Benchmark Suite

`MavenOperator.Tests.Performance/k6/comparison/`:

```
setup.js            # shared seed list, base URLs, auth headers
maven-operator.js   # k6 against MavenOperator Hosted repo
reposilite.js       # k6 against Reposilite in same cluster
compare.sh          # run both; produce summary.json
```

### 9.1  Scenarios

| Scenario | VUs | Duration | Description |
|----------|-----|----------|-------------|
| `download-small` | 100 | 2 min | 50 KB JAR — 10 k iterations |
| `download-large` | 20 | 2 min | 50 MB JAR — 200 iterations |
| `upload` | 10 | 2 min | PUT unique 50 KB JARs |
| `metadata` | 50 | 1 min | GET `maven-metadata.xml` |
| `mixed` | 40 | 5 min | 80 % download / 20 % upload |

### 9.2  Success Gates

| Metric | Gate |
|--------|------|
| p50 download latency | ≤ Reposilite p50 × 1.10 |
| p95 download latency | ≤ Reposilite p95 × 1.10 |
| Throughput (req/s) | ≥ Reposilite × 0.90 |
| Error rate | < 0.1 % |

### 9.3  CI Job

1. Spin up k3d with RWX-capable StorageClass.
2. Deploy MavenOperator + Reposilite.
3. Seed both via snapshot PVC import.
4. Run `compare.sh`; upload `summary.json` as artifact.
5. Fail CI if any gate is breached.

---

## 10  Testing

### 10.1  Unit Tests

| Test class | What it tests |
|------------|---------------|
| `ReposiliteApiSourceTests` | Directory walk, `sinceTimestamp`, group filter, retry on 5xx |
| `JFrogCloudApiSourceTests` | Flat-list parsing, group filter, token auth, `.asc` skipping |
| `PvcSnapshotSourceTests` | Filesystem walk, `reposiliteLayout` stripping, `mtime` filter, group glob |
| `DirectPvcSinkTests` | File write, `overwriteExisting: false` skip, directory creation, stream integrity |
| `HttpSinkTests` | PUT, HEAD-before-PUT, checksum validation, progress reporting |
| `ArtifactCrawlerTests` | Parallelism cap, error isolation per artifact |
| `ChecksumValidatorTests` | SHA-256 match, mismatch → error, missing checksum file |
| `MavenLayoutTranslatorTests` | Reposilite path stripping, separator normalisation, `.index`/`.cache` removal |
| `PvcAccessCheckerTests` | RWO-bound detection, RWX pass-through, no-pods case |

### 10.2  Integration Tests

#### Mode A — API Crawl

- **`ReposiliteApiImportDirectWriteTests`**
  - Spin up `docker.io/dzikoysk/reposilite:3` with 20 pre-published artifacts.
  - Run import Job against an RWX-backed Hosted repo.
  - Assert all 20 artifact files exist at correct paths on the target PVC.
  - Assert `status.transferMode == "directWrite"`.
  - Assert `mvn dependency:resolve` succeeds for all 20 artifacts.

- **`JFrogApiImportIntegrationTests`**
  - WireMock server simulating Artifactory storage + download APIs.
  - Assert correct crawl, group filter, and direct PVC write.

- **`ApiImportHttpFallbackTests`**
  - Hosted repo backed by RWO PVC already mounted by NGINX.
  - Assert operator detects RWO conflict → `HttpSink` fallback.
  - Assert `status.transferMode == "http"` and `Warning` condition present.
  - Assert all artifacts are resolvable after import.

#### Mode B — Snapshot / External PVC Clone

- **`PvcSnapshotReposiliteLayoutTests`**
  - Create source PVC with Reposilite-layout directory tree (20 artifacts).
  - Apply import CR with `pvcSnapshot.reposiliteLayout: true`.
  - Assert all 20 artifacts copied to target PVC at correct Maven paths.
  - Assert `maven-metadata.xml` files are **not** present on target.

- **`PvcSnapshotRawMavenLayoutTests`**
  - Source PVC with `reposiliteLayout: false`.
  - Assert paths copied verbatim without `/<repository>/` stripping.

- **`PvcSnapshotOverwriteExistingFalseTests`**
  - Pre-seed 5 artifacts on target PVC before run.
  - Assert those 5 are skipped; others copied.
  - Assert `status.artifactsCopied == 15`, `status.artifactsDiscovered == 20`.

- **`PvcSnapshotDryRunTests`**
  - `dryRun: true`; assert zero writes; `status.artifactsDiscovered == 20`.

- **`PvcSnapshotRwoConflictTests`**
  - Source PVC is RWO and bound to a running pod.
  - Assert `status.phase == Failed` with `Error` condition; no Job created.

#### Mode C — Live PVC Clone

- **`PvcLiveScaleDownImportTests`**
  - Deploy Reposilite with RWX PVC in k3d.
  - Apply import CR with `scaleDownDuration: 30s`.
  - Assert Reposilite scaled to 0 before Job starts (pod listing check).
  - Assert all artifacts copied to target PVC.
  - Assert Reposilite restored to original replica count after Job completion.

- **`PvcLiveFinalizerRestoreTests`**
  - Start import CR with scale-down; delete CR mid-Job.
  - Assert finalizer restores Reposilite replicas before CR is removed.

- **`PvcLiveConcurrentNoScaleDownTests`**
  - `scaleDownDuration: 0s` with RWX PVC.
  - Assert Job runs concurrently with Reposilite; `Warning` condition emitted.
  - Assert all artifacts fully copied (no corruption).

### 10.3  E2E Tests

| Test | Description |
|------|-------------|
| `ApiImportReposiliteE2ETest` | Real Reposilite → MavenOperator via REST + direct PVC write; `mvn dependency:resolve` |
| `SnapshotImportE2ETest` | Offline PVC snapshot → MavenOperator; `mvn dependency:resolve` |
| `LivePvcImportE2ETest` | Live Reposilite RWX PVC (scale-down); Reposilite restored; both repos resolvable post-import |
| `DryRunE2ETest` | `dryRun: true` across all three modes; zero writes; discovery counts correct |
| `PartialMigrationWithFiltersE2ETest` | `includeGroups: ["com.example.*"]`; only matching artifacts present on target |

### 10.4  Throughput Benchmark (BenchmarkDotNet)

- `ImportThroughputBenchmark`: `DirectPvcSink` vs `HttpSink` for a 1 000-artifact corpus.
- Success criterion: `DirectPvcSink` throughput ≥ 3× `HttpSink`.

---

## 11  New Projects & Files

| Path | Description |
|------|-------------|
| `MavenOperator.ImportJob/` | .NET 10 console app |
| `MavenOperator/Controllers/MavenRepositoryImportController.cs` | New controller |
| `MavenOperator/Entities/MavenRepositoryImportV1Alpha1.cs` | CRD entity + spec/status |
| `MavenOperator/Services/PvcAccessChecker.cs` | RWO conflict detection |
| `config/crds/mavenrepositoryimports.maven.operator.io.yaml` | Generated CRD YAML |
| `charts/maven-operator/crds/mavenrepositoryimports.yaml` | Helm CRD |
| `MavenOperator.Tests.Unit/ImportJob/` | Unit test classes |
| `MavenOperator.Tests.Integration/Import/` | Integration test classes |
| `MavenOperator.Tests.E2E/Import/` | E2E test classes |
| `MavenOperator.Tests.Performance/Benchmarks/ImportThroughputBenchmark.cs` | Throughput benchmark |
| `MavenOperator.Tests.Performance/k6/comparison/` | k6 comparison scripts |

---

## 12  Helm Chart Changes

```yaml
# values.yaml additions
storage:
  defaultAccessMode: ReadWriteMany   # Global default; existing repos unaffected unless PVC recreated

importJob:
  image:
    repository: ghcr.io/marchermans/maven-operator-import-job
    tag: ""            # defaults to .Chart.AppVersion
    pullPolicy: IfNotPresent
  resources:
    requests: { cpu: 250m, memory: 256Mi }
    limits:   { cpu: 1,    memory: 512Mi }
  serviceAccount:
    create: true       # needs Deployment scale + CR patch permissions
    name: ""
```

---

## 13  Out of Scope for Phase 7

- Import from Nexus Repository Manager.
- Import from S3 / object storage.
- Incremental / scheduled (CronJob) imports — use `filters.sinceTimestamp` manually.
- Artifact deduplication across repos.
- LDAP authentication (deferred to Phase 8).
