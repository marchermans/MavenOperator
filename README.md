# MavenOperator

A **Kubernetes Operator** written in C# (.NET 10) that manages self-hosted Maven
repositories as first-class Kubernetes resources.  
Declare a `MavenRepository` custom resource and the operator automatically
provisions NGINX-based storage pods, proxy-cache pods, or a virtual fan-out
aggregation proxy — complete with authentication, Ingress, PersistentVolumes, and
Prometheus metrics.

---

## Table of Contents

- [Concepts](#concepts)
- [Repository Types](#repository-types)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
  - [Quick start (Helm)](#quick-start-helm)
  - [Helm values reference](#helm-values-reference)
- [Usage](#usage)
  - [Hosted repository](#hosted-repository)
  - [Proxy repository](#proxy-repository)
  - [Virtual repository](#virtual-repository)
  - [Authentication](#authentication)
  - [Exposing via Ingress](#exposing-via-ingress)
  - [Using with Maven](#using-with-maven)
  - [Using with Gradle](#using-with-gradle)
- [Status & conditions](#status--conditions)
- [Metrics](#metrics)
- [Development](#development)
  - [Repository layout](#repository-layout)
  - [Building locally](#building-locally)
  - [Running the test suite](#running-the-test-suite)
  - [Releasing a new version](#releasing-a-new-version)
- [Architecture overview](#architecture-overview)
- [Roadmap](#roadmap)

---

## Concepts

| Term | Meaning |
|------|---------|
| **MavenRepository** | The single CRD that describes the desired state of a repo. |
| **Operator** | The controller loop (`MavenOperator`) that watches CRs and reconciles child resources. |
| **Virtual Proxy** | A separate ASP.NET Core sidecar (`MavenOperator.VirtualProxy`) that fans requests out to repo members and merges `maven-metadata.xml`. |

The CRD is the *single source of truth*. Every aspect of a repository's desired
state — type, storage, auth, upstream URL, members — lives in the spec.

---

## Repository Types

### Hosted
Stores artifacts on a PersistentVolume served by NGINX + WebDAV.  
Use this for internal snapshots and releases.

### Proxy
Caches artifacts from a remote upstream (e.g. Maven Central) via NGINX
`proxy_pass`. Downloads are transparently forwarded and cached locally.

### Virtual
Aggregates multiple Hosted and/or Proxy repositories.  
- Requests are fanned out to all members in parallel.  
- `maven-metadata.xml` responses are **merged** across all members, so
  your build tool always sees the union of available versions.  
- Uploads are forbidden (`405 Method Not Allowed`) — upload directly to a Hosted member.

---

## Prerequisites

| Requirement | Version |
|-------------|---------|
| Kubernetes | ≥ 1.28 |
| Helm | ≥ 3.14 |
| `kubectl` | matching your cluster version |
| Persistent storage provisioner | any (e.g. `local-path`, `longhorn`, cloud CSI) |

Optional but recommended:
- **Ingress controller** (e.g. NGINX Ingress, Traefik) — for external access.
- **cert-manager** — for automatic TLS on Ingress resources.
- **prometheus-operator** — for `ServiceMonitor`-based scraping.

---

## Installation

### Quick start (Helm)

```bash
# 1. Add the GHCR OCI registry (one-time)
helm registry login ghcr.io --username <your-github-username> \
  --password <your-github-pat-with-read:packages>

# 2. Install the operator into its own namespace
helm upgrade --install maven-operator \
  oci://ghcr.io/marchermans/charts/maven-operator \
  --namespace maven-operator-system \
  --create-namespace \
  --version 0.1.0
```

The chart installs:
- The operator `Deployment`
- A `ServiceAccount` + `ClusterRole` / `ClusterRoleBinding`
- A `Service` on port 9090 for Prometheus scraping

> **CRD lifecycle**: CRDs are bundled in `charts/maven-operator/crds/` and
> installed automatically by Helm on the first install.  Helm does **not**
> delete CRDs on `helm uninstall` — your `MavenRepository` objects persist.

---

### Helm values reference

| Key | Default | Description |
|-----|---------|-------------|
| `replicaCount` | `1` | Operator replicas (leader election keeps only 1 active). |
| `image.repository` | `ghcr.io/marchermans/maven-operator` | Operator image. |
| `image.tag` | `""` → `Chart.appVersion` | Pin to a specific digest/tag. |
| `virtualProxy.image.repository` | `ghcr.io/marchermans/maven-virtual-proxy` | Virtual proxy image. |
| `virtualProxy.image.tag` | `""` → `Chart.appVersion` | Pin to a specific digest/tag. |
| `resources.limits.cpu` | `500m` | |
| `resources.limits.memory` | `256Mi` | |
| `metrics.serviceMonitor.enabled` | `false` | Create a Prometheus `ServiceMonitor`. |
| `metrics.serviceMonitor.additionalLabels` | `{}` | Labels to match your Prometheus instance. |
| `rbac.create` | `true` | Create ClusterRole and binding. |

Full list: [`charts/maven-operator/values.yaml`](charts/maven-operator/values.yaml)

---

## Usage

All examples below create resources in a `my-repos` namespace:

```bash
kubectl create namespace my-repos
```

---

### Hosted repository

```yaml
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepository
metadata:
  name: releases
  namespace: my-repos
spec:
  type: Hosted
  storage:
    size: 20Gi
    storageClassName: standard   # omit to use the cluster default
    deletionPolicy: Retain       # Retain (default) | Delete
  auth:
    download:
      policy: Anonymous          # Anonymous | Authenticated
    upload:
      policy: Authenticated
      users:
        - secretRef: deploy-credentials
          role: Deployer         # Reader | Deployer (default) | Admin
```

The operator creates:
- A PVC `releases-data` (20 Gi)
- An NGINX deployment `releases-nginx`
- A Service `releases-svc` on port 80
- Separate htpasswd Secrets: `releases-download-htpasswd` and `releases-upload-htpasswd`

Artifacts are stored at the path `/repository/releases/<groupId>/<artifactId>/…`.

---

### Proxy repository

```yaml
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepository
metadata:
  name: maven-central-cache
  namespace: my-repos
spec:
  type: Proxy
  upstream:
    url: https://repo1.maven.org/maven2
  cache:
    size: 50Gi
  auth:
    download:
      policy: Anonymous
    upload:
      policy: Authenticated
      users:                  # upload == refresh cache on-demand (optional)
        - secretRef: admin-credentials
          role: Deployer
```

---

### Virtual repository

```yaml
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepository
metadata:
  name: public
  namespace: my-repos
spec:
  type: Virtual
  virtual:
    members:
      - name: releases
        namespace: my-repos
      - name: maven-central-cache
        namespace: my-repos
  auth:
    download:
      policy: Anonymous
```

Point your build tools at `public` and they transparently resolve artifacts from
both `releases` and `maven-central-cache`.

---

### Authentication

Authentication is configured through separate **download** and **upload** policies, allowing
independent authorization for reads vs. writes. Each policy supports three mechanisms:

1. **Anonymous** — no credentials required
2. **Authenticated** with credential users (HTTP Basic Auth)
3. **CI Trust** — OIDC JWT validation from GitHub Actions, GitLab CI, etc.
4. **Per-artifact ACLs** — enforce role-based path restrictions

#### Credential Users

Each credential user is defined as a Kubernetes `Secret` in the same namespace:

```bash
kubectl create secret generic deploy-credentials \
  --namespace my-repos \
  --from-literal=username=ci-deploy \
  --from-literal=password='s3cr3t!'
```

Then reference it by `secretRef`:

```yaml
auth:
  upload:
    policy: Authenticated
    users:
      - secretRef: deploy-credentials
        role: Deployer              # Reader | Deployer | Admin
```

The operator compiles all referenced Secrets into directional htpasswd files:
- `<name>-download-htpasswd` — for download policy
- `<name>-upload-htpasswd` — for upload policy

#### CI Trust (OIDC)

Grant upload access to CI workflows using GitHub Actions JWTs:

```yaml
auth:
  upload:
    policy: Authenticated
    ciTrust:
      - platform: GitHubActions
        role: Deployer
        claims:
          repository: "my-org/*"             # Glob-supported
          environment: production
```

#### Per-Artifact ACLs

Restrict access to specific artifact paths by role:

```yaml
auth:
  download:
    policy: Authenticated
    users:
      - secretRef: public-reader
        role: Reader
    acls:
      - path: "com/example/internal/**"
        roles: [Admin, Deployer]             # Reader cannot access
      - path: "com/example/public/**"
        roles: [Reader, Deployer, Admin]     # Everyone can access
  upload:
    policy: Authenticated
    users:
      - secretRef: deployer-creds
        role: Deployer
    acls:
      - path: "**"                           # All paths
        roles: [Deployer, Admin]             # Only these roles can upload
```

The operator updates htpasswd files automatically whenever a credential `Secret` changes.

---

### Exposing via Ingress

Add an `ingress` block to the spec:

```yaml
spec:
  # ... (type, storage, auth as above)
  ingress:
    enabled: true
    className: nginx
    host: maven.example.com
    tls:
      enabled: true
      secretName: maven-tls   # pre-created by cert-manager or manually
```

The operator creates an `Ingress` resource with appropriate annotations for
WebDAV (PUT/DELETE) pass-through on Hosted repos.

---

### Using with Maven

Add the repository to your `~/.m2/settings.xml`:

```xml
<settings>
  <servers>
    <server>
      <id>my-releases</id>
      <username>ci-deploy</username>
      <password>s3cr3t!</password>
    </server>
  </servers>
</settings>
```

In your `pom.xml`:

```xml
<distributionManagement>
  <repository>
    <id>my-releases</id>
    <url>http://releases-svc.my-repos.svc.cluster.local/repository/releases</url>
  </repository>
</distributionManagement>

<repositories>
  <repository>
    <id>public</id>
    <url>http://public-svc.my-repos.svc.cluster.local/repository/public</url>
  </repository>
</repositories>
```

---

### Using with Gradle

```groovy
// build.gradle (Groovy DSL)
repositories {
    maven {
        url "http://public-svc.my-repos.svc.cluster.local/repository/public"
    }
}

publishing {
    repositories {
        maven {
            url "http://releases-svc.my-repos.svc.cluster.local/repository/releases"
            credentials {
                username = findProperty("mavenUser") ?: "ci-deploy"
                password = findProperty("mavenPass") ?: ""
            }
        }
    }
}
```

---

## Status & conditions

The operator writes reconciliation state back into `status`:

```bash
kubectl get mavenrepository releases -n my-repos -o yaml
```

```yaml
status:
  phase: Ready          # Pending | Provisioning | Ready | Failed | Terminating
  conditions:
    - type: Ready
      status: "True"
      reason: ReconcileSuccess
      lastTransitionTime: "2026-05-16T10:00:00Z"
```

Errors are also emitted as Kubernetes Events:

```bash
kubectl events --namespace my-repos --for mavenrepository/releases
```

---

## Metrics

Both the operator and the virtual proxy expose Prometheus metrics.

| Component | Endpoint | Port |
|-----------|----------|------|
| Operator | `/metrics` | `9090` |
| Virtual Proxy | `/metrics` | `8080` |

### Operator metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `mavenoperator_reconcile_duration_seconds` | Histogram | `repo_name`, `repo_type`, `success` | Reconcile loop latency |
| `mavenoperator_reconcile_total` | Counter | `repo_name`, `repo_type`, `success` | Total reconcile invocations |
| `mavenoperator_resource_apply_total` | Counter | `repo_name`, `repo_type`, `resource_kind` | Child resources applied |
| `mavenoperator_repository_count` | Gauge | `repo_type`, `phase` | Number of repositories per type/phase |

### Virtual proxy metrics

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `virtual_proxy_requests_total` | Counter | `repo_name`, `asset_path`, `asset_type`, `status_code` | Per-asset download counter |
| `virtual_proxy_member_request_duration_seconds` | Histogram | `repo_name`, `member_name`, `success` | Per-member fetch latency |
| `virtual_proxy_metadata_merge_duration_seconds` | Histogram | `repo_name` | `maven-metadata.xml` merge latency |
| `virtual_proxy_metadata_merge_member_count` | Histogram | `repo_name` | Members queried per merge |

`asset_type` is one of: `jar`, `pom`, `metadata`, `checksum`, `other`.

### Enabling ServiceMonitor (prometheus-operator)

```bash
helm upgrade maven-operator oci://ghcr.io/marchermans/charts/maven-operator \
  --reuse-values \
  --set metrics.serviceMonitor.enabled=true \
  --set metrics.serviceMonitor.additionalLabels.release=kube-prometheus-stack
```

---

## Development

### Repository layout

```
MavenOperator/
├── VERSION                          # Base semver (bump before tagging a release)
├── config/crds/                     # Generated CRD YAML (committed)
├── charts/maven-operator/           # Helm chart
├── MavenOperator/                   # Operator (KubeOps, .NET 10)
│   ├── Controllers/                 # KubeOps reconciliation entry points
│   ├── Entities/                    # CRD entity + Spec/Status POCOs
│   ├── Reconcilers/                 # One reconciler per repo type
│   ├── Services/                    # NginxConfigRenderer, HtpasswdService, …
│   └── Templates/                   # Scriban NGINX config templates
├── MavenOperator.VirtualProxy/      # Virtual-repo aggregation proxy
├── MavenOperator.Tests.Unit/        # Pure unit tests (no cluster)
├── MavenOperator.Tests.Unit.VirtualProxy/
├── MavenOperator.Tests.Integration/ # Reconciler tests against k3d
├── MavenOperator.Tests.E2E/         # End-to-end Maven/Gradle round-trips
├── MavenOperator.Tests.Performance/ # BenchmarkDotNet + k6 load tests
└── scripts/
    └── run-tests.sh                 # Developer test driver
```

### Building locally

```bash
# Restore & build everything
dotnet restore
dotnet build

# Build Docker images
docker build -t maven-operator:dev       -f MavenOperator/Dockerfile .
docker build -t maven-virtual-proxy:dev  -f MavenOperator.VirtualProxy/Dockerfile .
```

### Running the test suite

The `scripts/run-tests.sh` script is the single entry point for all test suites.
It manages a local k3d cluster automatically.

```bash
# Unit tests only (no cluster needed) — fastest feedback loop
./scripts/run-tests.sh unit

# Unit + integration
./scripts/run-tests.sh unit integration

# Everything except k6 load tests (quick CI-equivalent)
./scripts/run-tests.sh all --fast

# Full suite including k6 load tests, then tear everything down
./scripts/run-tests.sh all --cleanup

# E2E against a pre-existing cluster (skip cluster creation)
KUBECONFIG=~/.kube/my-cluster.yaml ./scripts/run-tests.sh e2e

# k6 load tests only (requires a running cluster with the operator deployed)
./scripts/run-tests.sh performance --perf-mode load
```

#### Prerequisites for cluster-based suites

| Tool | Install |
|------|---------|
| k3d ≥ 5 | `curl -s https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh \| bash` |
| kubectl | `https://kubernetes.io/docs/tasks/tools/` |
| Docker | — |
| Maven (E2E) | `sudo apt install maven` / `brew install maven` |
| Gradle (E2E) | via Gradle wrapper in fixtures |
| k6 (load) | `https://grafana.com/docs/k6/latest/set-up/install-k6/` |

### Releasing a new version

Versioning is **fully automatic** — no `VERSION` file or manual bump needed.
The CI `version` job uses [`paulhatch/semantic-version`](https://github.com/PaulHatch/semantic-version)
to derive the next version from git tag history and conventional-commit messages.

#### How the version is computed

| Commit type | Effect |
|-------------|--------|
| `fix:`, `chore:`, etc. | patch bump (`1.0.0` → `1.0.1`) |
| `feat:` or `feat(scope):` | minor bump (`1.0.0` → `1.1.0`) |
| `feat!:`, `fix!:`, `BREAKING CHANGE:` | major bump (`1.0.0` → `2.0.0`) |

**Untagged commits** produce a pre-release version: `1.2.3-pre.N`  
(N = number of commits since the last tag).

**Tagged commits** (`v1.2.3`) produce the clean release version: `1.2.3`.

#### Shipping a release

1. Write commits following [Conventional Commits](https://www.conventionalcommits.org/) — the type prefix determines which semver component is bumped.
2. When ready to release, tag the commit and push:
   ```bash
   git tag v1.2.0
   git push origin v1.2.0
   ```
3. CI will automatically:
   - Compute version `1.2.0` (exact release — no `-pre.N` suffix).
   - Run all tests.
   - Build and push both Docker images tagged `1.2.0`, `1.2`, and `latest`.
   - Stamp `version: "1.2.0"` and `appVersion: "1.2.0"` into `Chart.yaml`.
   - Package and push the Helm chart to `oci://ghcr.io/marchermans/charts`.

Snapshot builds on `main` receive the pre-release tag (e.g. `1.2.0-pre.4`)
and are additionally tagged with the branch name (`main`).

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────┐
│  Kubernetes API                                          │
│  ┌─────────────────────────────────────────────────┐    │
│  │ MavenRepository CRD  (source of truth)          │    │
│  └─────────────────────────────────────────────────┘    │
│              ▲ watch / patch status                      │
│              │                                           │
│  ┌───────────┴──────────────────────────────────────┐   │
│  │ MavenOperator (KubeOps controller loop)          │   │
│  │  HostedRepositoryReconciler                      │   │
│  │  ProxyRepositoryReconciler                       │   │
│  │  VirtualRepositoryReconciler                     │   │
│  └──────────────────────────────────────────────────┘   │
│    creates / patches child resources with owner refs     │
│                                                          │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ NGINX pod    │  │ NGINX pod    │  │ VirtualProxy  │  │
│  │ (Hosted)     │  │ (Proxy)      │  │ pod (Virtual) │  │
│  │ WebDAV + PVC │  │ proxy_pass   │  │ fan-out +     │  │
│  └──────────────┘  └──────────────┘  │ metadata merge│  │
│                                      └───────────────┘  │
└─────────────────────────────────────────────────────────┘
```

Each child workload has an **owner reference** pointing to the parent
`MavenRepository`, so deleting the CR cascades to all managed Pods, Services,
ConfigMaps, and Secrets (except PVCs when `deletionPolicy: Retain`).

---

## Roadmap

| Phase | Status | Description |
|-------|--------|-------------|
| 0 | ✅ Done | KubeOps wired up, CRD entity defined |
| 1 | ✅ Done | Hosted repository (mvn deploy + resolve) |
| 2 | ✅ Done | Proxy repository (cache from Maven Central) |
| 3 | ✅ Done | Virtual repository (fan-out + metadata merge) |
| 4 | ✅ Done | CEL validation, Events, Secret watch, deletionPolicy, Ingress |
| 5 | ✅ Done | Prometheus metrics, Helm chart, GitHub Actions CI |
| 6A | ✅ Done | **Deep observability** — per-artifact NGINX metrics via `nginx-prometheus-exporter` + `mtail` sidecars; `PodMonitor`; Grafana dashboards; PrometheusRule alert rules |
| 6B | ✅ Done | **Enhanced authentication** — role-based access (reader/deployer/admin); CI platform OIDC trust (GitHub Actions & GitLab CI JWTs, no pre-provisioned secrets); per-artifact-path ACLs |
| 7  | 🔜 Planned | **Import & Migration** — `MavenRepositoryImport` CRD; three transfer modes: (A) REST API crawl from Reposilite/JFrog Cloud → direct PVC write (no HTTP round-trip), (B) offline PVC snapshot clone, (C) live Reposilite PVC clone with optional scale-down; RWX storage promoted to default; k6 comparison benchmarks proving MavenOperator matches or beats Reposilite throughput |

