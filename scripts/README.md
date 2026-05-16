# Developer Test Runner

`scripts/run-tests.sh` is a single entry point for running any combination of MavenOperator's three test tiers. It handles all prerequisite checking, cluster lifecycle, and environment variable wiring — you only need to pick what you want to run.

---

## Quick start

```bash
# Unit tests only — no cluster, no container runtime needed
./scripts/run-tests.sh

# Unit + integration (spins up k3d automatically)
./scripts/run-tests.sh unit integration

# Full suite — unit + integration + E2E, clean up the cluster after
./scripts/run-tests.sh all --cleanup

# Re-run integration against an existing cluster (KUBECONFIG already set)
./scripts/run-tests.sh integration --no-build
```

---

## Suites

| Suite | What runs | External requirements |
|---|---|---|
| `unit` | `[Fact]` / `[Theory]` in `MavenOperator.Tests.Unit` | .NET 10 SDK only |
| `integration` | `[IntegrationFact]` in `MavenOperator.Tests.Integration` | .NET 10, kubectl, k3d, podman or docker |
| `e2e` | `[E2EFact]` in `MavenOperator.Tests.E2E` | Everything above + JDK 21 |
| `all` | Shortcut for `unit integration e2e` | |

---

## Options

| Flag | Default | Description |
|---|---|---|
| `--cleanup` | off | Delete the k3d cluster when the script exits |
| `--cluster <name>` | `maven-operator-test` | k3d cluster name (also via `K3D_CLUSTER_NAME`) |
| `--namespace <ns>` | `maven-operator-system` | Kubernetes namespace for the operator |
| `--image <tag>` | `maven-operator:dev` | Container image tag to build and load |
| `--no-build` | off | Skip `dotnet build` — use already-built binaries |
| `--filter <expr>` | — | Extra `--filter` expression forwarded to `dotnet test` |

---

## How it works per suite

### `unit`
1. Checks: `dotnet` ≥ 10
2. Runs `dotnet build` (unless `--no-build`)
3. Runs `dotnet test MavenOperator.Tests.Unit --no-build`

### `integration`
1. Checks: `dotnet`, `kubectl`, `k3d`, container runtime
2. If `k3d` is missing, attempts to install it via the official installer script
3. Creates a k3d cluster `maven-operator-test` (skipped if it already exists)
4. Applies operator CRDs from the Debug build output
5. Runs tests with `INTEGRATION_TESTS=true` which un-gates `[IntegrationFact]`

> **Bring your own cluster** — set `KUBECONFIG` before calling the script and k3d will be skipped entirely.

### `e2e`
All of the above, plus:
1. Checks: JDK 21+
2. Builds the operator container image with podman/docker
3. Loads the image into the k3d cluster (`k3d image import`)
4. Applies RBAC and deploys the operator `Deployment`
5. Waits for the operator to roll out
6. Runs tests with `E2E_TESTS=true` which un-gates `[E2EFact]`

---

## File layout

```
scripts/
  run-tests.sh       ← main entry point (chmod +x)
  lib/
    log.sh           ← coloured logging helpers
    checks.sh        ← per-tool prerequisite functions
    cluster.sh       ← k3d cluster lifecycle (up/down/crd/rbac/image/deploy)
```

---

## Environment variables

| Variable | Description |
|---|---|
| `K3D_CLUSTER_NAME` | k3d cluster name (overrides `--cluster`) |
| `OPERATOR_IMAGE` | Container image tag (overrides `--image`) |
| `OPERATOR_NAMESPACE` | Kubernetes namespace (overrides `--namespace`) |
| `KUBECONFIG` | If set, k3d is not touched; tests use this config |
| `INTEGRATION_TESTS` | Set to `true` internally by the script to unlock integration tests |
| `E2E_TESTS` | Set to `true` internally by the script to unlock E2E tests |

