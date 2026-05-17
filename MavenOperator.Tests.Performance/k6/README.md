# k6 Load & Stress Tests — MavenOperator
## Prerequisites
Install k6: https://k6.io/docs/get-started/installation/
```bash
# Fedora/RHEL
sudo dnf install https://dl.k6.io/rpm/repo.rpm && sudo dnf install k6
# macOS
brew install k6
# Ubuntu/Debian
sudo apt-key adv --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D69
echo "deb https://dl.k6.io/deb stable main" | sudo tee /etc/apt/sources.list.d/k6.list
sudo apt-get update && sudo apt-get install k6
```
---
## Tests
### hosted-load-test.js — General load test
Mixed read/write workload simulating everyday Maven client traffic:
- **10 VUs sustained** — realistic developer team
- **90 % downloads / 10 % uploads**
- Validates steady-state throughput and error rate
```bash
# Start a port-forward first
kubectl port-forward -n maven-perf svc/perf-hosted-svc 8080:80 &
k6 run k6/hosted-load-test.js \
  -e REPO_URL=http://localhost:8080/repository/perf-hosted \
  -e AUTH_DOWNLOAD=anon:anon \
  -e AUTH_UPLOAD=anon:anon
```
| Threshold | Gate |
|-----------|------|
| p(95) download latency | < 200 ms |
| p(95) upload latency   | < 500 ms |
| HTTP error rate        | < 1 %    |
---
### concurrent-download-test.js — High-concurrency download stress test
Two back-to-back scenarios with **100+ simultaneous clients**:
#### Scenario A — Hot artifact (thundering herd)
All VUs request **the exact same file** at the same time.
Tests NGINX file-descriptor handling, accept-queue depth, and connection back-pressure when every client races for one path.
- **120 VUs** ramp up over 20 s, sustained 60 s
- All traffic hits `io/mavenoperator/perf/hot/1.0.0/hot-artifact-1.0.0.jar`
| Threshold          | Gate   |
|--------------------|--------|
| p(95) latency      | < 300 ms |
| p(99) latency      | < 500 ms |
| error rate         | < 1 %  |
#### Scenario B — Catalogue sweep (path diversity)
Each VU picks a **random artifact from a pre-seeded catalogue** of N distinct files.
Simulates `mvn dependency:resolve` pulling transitive dependencies, maximising filesystem path scatter.
- **100 VUs** ramp up over 20 s, sustained 60 s
- Randomly selects from `CATALOGUE_SIZE` distinct artifacts (default 50)
- Each artifact is `ARTIFACT_SIZE_BYTES` in size (default 100 KB)
| Threshold          | Gate   |
|--------------------|--------|
| p(95) latency      | < 500 ms |
| p(99) latency      | < 1 000 ms |
| error rate         | < 1 %  |
```bash
# Run with defaults (120 hot VUs + 100 catalogue VUs, 50 x 100 KB artifacts)
k6 run k6/concurrent-download-test.js \
  -e REPO_URL=http://localhost:8080/repository/perf-hosted \
  -e AUTH_DOWNLOAD=anon:anon \
  -e AUTH_UPLOAD=anon:anon
# Tune VU counts and artifact parameters
k6 run k6/concurrent-download-test.js \
  -e REPO_URL=http://localhost:8080/repository/perf-hosted \
  -e AUTH_DOWNLOAD=anon:anon \
  -e AUTH_UPLOAD=anon:anon \
  -e HOT_VUS=200 \
  -e CATALOGUE_VUS=150 \
  -e CATALOGUE_SIZE=100 \
  -e ARTIFACT_SIZE_BYTES=524288
# Save JSON results for trend analysis
k6 run k6/concurrent-download-test.js \
  -e REPO_URL=http://localhost:8080/repository/perf-hosted \
  -e AUTH_DOWNLOAD=anon:anon \
  -e AUTH_UPLOAD=anon:anon \
  --out json=k6-concurrent-summary.json
```
---
## Running via the test script
```bash
# Run all k6 tests against a managed k3d cluster (builds + deploys automatically)
./scripts/run-tests.sh performance --perf-mode load
# Run alongside all other suites
./scripts/run-tests.sh all --cleanup
```
The script provisions a `maven-perf/perf-hosted` Anonymous repository, port-forwards it,
runs both k6 tests sequentially, and saves JSON summaries to `.benchmarks/`.
---
## Reposilite comparison (quick run)
Use `comparison/compare.sh` to run the same k6 scenario against MavenOperator and Reposilite,
then generate a side-by-side summary.

```bash
cd MavenOperator.Tests.Performance/k6/comparison
./compare.sh \
  --maven-operator-url http://localhost:8081 \
  --reposilite-url http://localhost:8082 \
  --repository releases \
  --enable-upload-scenario false \
  --parallel-scenarios false \
  --download-user reader \
  --download-pass secret
```

Outputs are written in the same directory:
- `summary-maven-operator.json`
- `summary-reposilite.json`
- `summary.json` (gate result + side-by-side metrics)

Exit code semantics:
- `0` = comparison gates passed
- `1` = one or more gates failed

The `--repository` value must match both seeded targets (Hosted repo name in MavenOperator
and path prefix in Reposilite). Default: `releases`.

`--enable-upload-scenario` defaults to `false` (download + metadata comparison only),
which works with default Reposilite setup out of the box.

`--parallel-scenarios` defaults to `false`, so scenarios run sequentially to avoid
resource contention skew on local k3d clusters. Set `true` only when intentionally
stress-testing with compounded concurrency.

Comparison workloads are also paced by default (`THINK_TIME_SECONDS=0.05`) to avoid
request-storm artifacts (timeouts/resets) from local tunnels. You can tune intensity via env vars,
for example: `DOWNLOAD_SMALL_VUS`, `DOWNLOAD_LARGE_VUS`, `METADATA_VUS`, `MIXED_VUS`, and
`THINK_TIME_SECONDS`.

### Fully automated (cluster + deploy + seed + run + cleanup)
Use the wrapper script when you want a one-command comparison run from scratch:

```bash
./scripts/run-k6-comparison.sh
```

What it does automatically:
- Creates a k3d cluster
- Loads operator/virtual-proxy/sidecar images and Reposilite image
- Deploys MavenOperator + Reposilite
- Creates a Hosted repo (`releases`) and seeds both targets with benchmark artifacts
- Runs `comparison/compare.sh`
- Saves outputs under `.benchmarks/k6-comparison-<timestamp>/`
- Deletes the cluster on exit

Keep the cluster around for debugging:

```bash
./scripts/run-k6-comparison.sh --keep-cluster
```

By default, the automated runner uses `benchmark:benchmark` as comparison credentials
(it configures a temporary Reposilite token with the same pair). Override if needed:

```bash
DOWNLOAD_USER=reader
DOWNLOAD_PASS=secret
./scripts/run-k6-comparison.sh
```
---
## CI gates (thresholds)
| Test            | Metric                 | Gate       |
|-----------------|------------------------|------------|
| General load    | p(95) download latency | < 200 ms   |
| General load    | p(95) upload latency   | < 500 ms   |
| General load    | HTTP error rate        | < 1 %      |
| Hot artifact    | p(95) download latency | < 300 ms   |
| Hot artifact    | p(99) download latency | < 500 ms   |
| Hot artifact    | error rate             | < 1 %      |
| Catalogue sweep | p(95) download latency | < 500 ms   |
| Catalogue sweep | p(99) download latency | < 1 000 ms |
| Catalogue sweep | error rate             | < 1 %      |
Baseline results are checked into `.benchmarks/` and compared on every release.
