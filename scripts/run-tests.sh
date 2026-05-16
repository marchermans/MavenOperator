#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# run-tests.sh — Developer test driver for MavenOperator
#
# USAGE
#   ./scripts/run-tests.sh [OPTIONS] [SUITES...]
#
# SUITES (default: unit)
#   unit          Pure unit tests — no cluster needed.
#   integration   Reconciler tests against a real k3d cluster.
#   e2e           Full Maven/Gradle client round-trips (requires the operator
#                 to be deployed in the cluster).
#   performance   Three sub-modes controlled by --perf-mode:
#                   smoke      — xUnit ceiling / regression tests (default)
#                   benchmark  — BenchmarkDotNet micro-benchmarks (Release build)
#                   load       — k6 HTTP load test against a live repository
#                   all        — smoke + benchmark + load
#                 Select sub-modes with --perf-mode smoke|benchmark|load|all
#   all           Shortcut for: unit integration e2e performance (--perf-mode all)
#                 Runs EVERY test including BenchmarkDotNet and k6 load tests.
#                 Use --fast to skip slow perf sub-modes (smoke only).
#   cleanup       Tear down the k3d cluster and wipe all operator namespaces.
#                 Can be run standalone: ./scripts/run-tests.sh cleanup
#                 Equivalent to any other suite with --cleanup, but without running tests.
#
# OPTIONS
#   --cleanup          Delete the k3d cluster when the script exits.
#   --cluster <name>   k3d cluster name          (default: maven-operator-test)
#   --namespace <ns>   Operator namespace         (default: maven-operator-system)
#   --image <tag>      Operator container image   (default: maven-operator:dev)
#   --proxy-image <tag> Virtual proxy image        (default: maven-virtual-proxy:dev)
#   --no-build         Skip 'dotnet build' step (use existing binaries).
#   --filter <expr>    Extra --filter expression passed to dotnet test.
#   --perf-mode <m>    Performance sub-modes: smoke (default), benchmark, load, all
#   --fast             When combined with 'all', run only perf smoke tests (skip
#                      BenchmarkDotNet and k6).  Equivalent to: all --perf-mode smoke
#   -h, --help         Print this help and exit.
#
# ENVIRONMENT
#   K3D_CLUSTER_NAME   Override cluster name (alternative to --cluster).
#   OPERATOR_IMAGE     Override image tag.
#   VIRTUAL_PROXY_IMAGE Override virtual proxy image tag.
#   OPERATOR_NAMESPACE Override operator namespace.
#   KUBECONFIG         If set, the integration/e2e tests use this config
#                      instead of spinning up k3d.
#
# EXAMPLES
#   ./scripts/run-tests.sh                      # run unit tests only
#   ./scripts/run-tests.sh unit integration     # unit + integration
#   ./scripts/run-tests.sh all --cleanup        # EVERYTHING: unit+int+e2e+perf(all), tear down
#   ./scripts/run-tests.sh cleanup              # tear down cluster only, no tests
#   ./scripts/run-tests.sh cleanup --cluster my-cluster  # tear down a named cluster
#   ./scripts/run-tests.sh all --fast           # unit+int+e2e+perf(smoke only) — quick
#   ./scripts/run-tests.sh e2e --no-build       # e2e with pre-built image
#   ./scripts/run-tests.sh performance          # perf smoke tests (xUnit, no cluster)
#   ./scripts/run-tests.sh performance --perf-mode benchmark  # BenchmarkDotNet only
#   ./scripts/run-tests.sh performance --perf-mode load       # k6 load tests only
#   ./scripts/run-tests.sh performance --perf-mode all        # smoke + benchmark + k6
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── Locate repository root ────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
export REPO_ROOT

# ── Source helpers ────────────────────────────────────────────────────────────
# shellcheck source=lib/log.sh
source "${SCRIPT_DIR}/lib/log.sh"
# shellcheck source=lib/checks.sh
source "${SCRIPT_DIR}/lib/checks.sh"
# shellcheck source=lib/cluster.sh
source "${SCRIPT_DIR}/lib/cluster.sh"

# ── Defaults ──────────────────────────────────────────────────────────────────
SUITES=()
OPT_CLEANUP=false
OPT_NO_BUILD=false
OPT_FILTER=""
PERF_MODE="smoke"   # smoke | benchmark | load | all
OPT_FAST=false
# Remember whether the caller supplied an external KUBECONFIG before we touch it.
_EXTERNAL_KUBECONFIG="${KUBECONFIG:-}"
K3D_CLUSTER_NAME="${K3D_CLUSTER_NAME:-maven-operator-test}"
OPERATOR_NAMESPACE="${OPERATOR_NAMESPACE:-maven-operator-system}"
OPERATOR_IMAGE="${OPERATOR_IMAGE:-maven-operator:dev}"
VIRTUAL_PROXY_IMAGE="${VIRTUAL_PROXY_IMAGE:-maven-virtual-proxy:dev}"
export K3D_CLUSTER_NAME OPERATOR_NAMESPACE OPERATOR_IMAGE VIRTUAL_PROXY_IMAGE

# ── Argument parsing ──────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case "$1" in
    unit|integration|e2e|performance|cleanup)
      SUITES+=("$1"); shift ;;
    all)
      SUITES=(unit integration e2e performance)
      # 'all' implies all performance sub-modes unless --fast overrides later.
      PERF_MODE="all"
      shift ;;
    --fast)
      # Override perf mode back to smoke-only for a quick all-suite run.
      OPT_FAST=true; PERF_MODE="smoke"; shift ;;
    --cleanup)
      OPT_CLEANUP=true; export K3D_CLEANUP=true; shift ;;
    --cluster)
      K3D_CLUSTER_NAME="$2"; shift 2 ;;
    --namespace)
      OPERATOR_NAMESPACE="$2"; shift 2 ;;
    --image)
      OPERATOR_IMAGE="$2"; shift 2 ;;
    --proxy-image)
      VIRTUAL_PROXY_IMAGE="$2"; shift 2 ;;
    --no-build)
      OPT_NO_BUILD=true; shift ;;
    --filter)
      OPT_FILTER="$2"; shift 2 ;;
    --perf-mode)
      PERF_MODE="$2"; shift 2 ;;
    -h|--help)
      sed -n '/^# USAGE/,/^# ─\{10\}/p' "$0" | sed 's/^# \?//'
      exit 0 ;;
    *)
      log_error "Unknown argument: $1"
      exit 1 ;;
  esac
done

# Default to unit tests if nothing specified
[[ ${#SUITES[@]} -eq 0 ]] && SUITES=(unit)

# Deduplicate while preserving order
mapfile -t SUITES < <(printf '%s\n' "${SUITES[@]}" | awk '!seen[$0]++')

# ── Cleanup trap (only if cluster management is needed) ───────────────────────
_needs_cluster() {
  for s in "${SUITES[@]}"; do
    [[ "$s" == integration || "$s" == e2e || "$s" == cleanup ]] && return 0
    # performance with load mode needs a cluster too
    [[ "$s" == performance && ( "$PERF_MODE" == load || "$PERF_MODE" == all ) ]] && return 0
  done
  return 1
}

if _needs_cluster && [[ "${OPT_CLEANUP}" == "true" ]]; then
  # Don't add the EXIT trap when the 'cleanup' suite is explicitly listed —
  # run_cleanup() will call cluster_down directly, avoiding a double-teardown.
  _has_cleanup_suite=false
  for _s in "${SUITES[@]}"; do [[ "$_s" == cleanup ]] && _has_cleanup_suite=true; done
  if [[ "$_has_cleanup_suite" == "false" ]]; then
    trap 'cluster_down' EXIT
  fi
fi

# Track overall exit code
OVERALL_EXIT=0

# ── Helper: dotnet test wrapper ───────────────────────────────────────────────
# Usage: run_dotnet_test <project-dir> <label> [ENV=val ...]
# ENV=val pairs are passed as environment variables to dotnet test.
run_dotnet_test() {
  local project="$1"
  local label="$2"
  shift 2
  local -a env_pairs=("$@")

  # Optionally append a user-supplied filter
  local -a filter_args=()
  [[ -n "${OPT_FILTER}" ]] && filter_args=(--filter "${OPT_FILTER}")

  log_step "dotnet test ${project}  (${label})"
  if env "${env_pairs[@]}" dotnet test "${REPO_ROOT}/${project}" \
      --no-build \
      --logger "console;verbosity=normal" \
      "${filter_args[@]}"; then
    log_ok "${label} tests PASSED"
  else
    log_error "${label} tests FAILED"
    OVERALL_EXIT=1
  fi
}

# ═════════════════════════════════════════════════════════════════════════════
# SUITE: cleanup
# Tears down the k3d cluster and deletes all operator-managed namespaces.
# Safe to run standalone: ./scripts/run-tests.sh cleanup
# ═════════════════════════════════════════════════════════════════════════════
run_cleanup() {
  log_section "Suite: cleanup"

  # Always enable cluster deletion for this suite.
  export K3D_CLEANUP=true

  # Delete the k3d cluster (delegates to cluster_down which checks K3D_CLEANUP).
  cluster_down

  # Also remove the operator namespace if kubectl is available.
  if command -v kubectl &>/dev/null && [[ -n "${KUBECONFIG:-}" ]] && [[ -f "${KUBECONFIG}" ]]; then
    local ns="${OPERATOR_NAMESPACE}"
    if kubectl get namespace "$ns" &>/dev/null 2>&1; then
      log_step "Deleting operator namespace '${ns}'..."
      kubectl delete namespace "$ns" --ignore-not-found &>/dev/null || true
      log_ok "Namespace '${ns}' deleted."
    else
      log_info "Operator namespace '${ns}' does not exist — nothing to delete."
    fi
  fi

  # Clean up the tmp kubeconfig.
  local kc="${REPO_ROOT}/.tmp/kubeconfig-${K3D_CLUSTER_NAME}.yaml"
  if [[ -f "$kc" ]]; then
    rm -f "$kc"
    log_ok "Removed kubeconfig ${kc}"
  fi

  log_ok "Cleanup complete."
}

# ── Main ──────────────────────────────────────────────────────────────────────
build_solution() {
  log_section "Building solution"
  dotnet build "${REPO_ROOT}/MavenOperator.slnx" --configuration Debug -v minimal
  log_ok "Build succeeded."
}

# ═════════════════════════════════════════════════════════════════════════════
# SUITE: unit
# Plain [Fact]/[Theory] tests — no env var gating, no cluster needed.
# ═════════════════════════════════════════════════════════════════════════════
run_unit() {
  log_section "Suite: unit"
  check_unit_prereqs || { log_error "Unit prereqs not met."; OVERALL_EXIT=1; return; }

  [[ "${OPT_NO_BUILD}" == "false" ]] && build_solution

  run_dotnet_test "MavenOperator.Tests.Unit" "Unit"
}

# ═════════════════════════════════════════════════════════════════════════════
# SUITE: integration
# [IntegrationFact] tests gated on INTEGRATION_TESTS=true.
# Requires a Kubernetes cluster — spins up k3d if KUBECONFIG is not set.
# ═════════════════════════════════════════════════════════════════════════════
run_integration() {
  log_section "Suite: integration"
  check_integration_prereqs || { log_error "Integration prereqs not met."; OVERALL_EXIT=1; return; }

  [[ "${OPT_NO_BUILD}" == "false" ]] && build_solution

  # Spin up k3d only if no external KUBECONFIG was supplied by the caller.
  if [[ -z "${_EXTERNAL_KUBECONFIG}" ]]; then
    cluster_up
    cluster_apply_crds
  else
    log_info "Using existing KUBECONFIG: ${KUBECONFIG}"
    cluster_apply_crds
  fi

  # Export KUBECONFIG so the child dotnet process inherits it automatically.
  export KUBECONFIG

  run_dotnet_test "MavenOperator.Tests.Integration" "Integration" "INTEGRATION_TESTS=true"
}

# ═════════════════════════════════════════════════════════════════════════════
# SUITE: e2e
# [E2EFact] tests gated on E2E_TESTS=true.
# Requires a cluster with the operator deployed and the repository service
# accessible. Builds + loads the operator image into k3d automatically.
# ═════════════════════════════════════════════════════════════════════════════
run_e2e() {
  log_section "Suite: e2e"
  check_e2e_prereqs || { log_error "E2E prereqs not met."; OVERALL_EXIT=1; return; }

  [[ "${OPT_NO_BUILD}" == "false" ]] && build_solution

  # Use an external cluster only if one was provided by the caller *before*
  # this script ran (i.e., not one we created ourselves for the integration suite).
  if [[ -z "${_EXTERNAL_KUBECONFIG}" ]]; then
    # We own the cluster lifecycle — bring it up (idempotent) and deploy the operator.
    cluster_up
    cluster_apply_crds
    cluster_apply_rbac
    cluster_load_operator_image
    cluster_load_virtual_proxy_image
    cluster_deploy_operator
  else
    log_info "Using external KUBECONFIG: ${KUBECONFIG}"
    log_warn "Assuming operator is already deployed in cluster."
  fi

  # Export KUBECONFIG so the child dotnet process inherits it automatically.
  export KUBECONFIG

  run_dotnet_test "MavenOperator.Tests.E2E" "E2E" "E2E_TESTS=true"
}

# ═════════════════════════════════════════════════════════════════════════════
# SUITE: performance
# Three sub-modes controlled by --perf-mode:
#   smoke     — xUnit ceiling tests (fast, no cluster, default)
#   benchmark — BenchmarkDotNet micro-benchmarks (Release build, no cluster)
#   load      — k6 HTTP load test against a live repository
#   all       — smoke + benchmark + load
# ═════════════════════════════════════════════════════════════════════════════
run_performance() {
  log_section "Suite: performance  (mode: ${PERF_MODE})"

  local run_smoke=false run_bench=false run_load=false
  case "$PERF_MODE" in
    smoke)     run_smoke=true ;;
    benchmark) run_bench=true ;;
    load)      run_load=true  ;;
    all)       run_smoke=true; run_bench=true; run_load=true ;;
    *)
      log_error "Unknown --perf-mode '${PERF_MODE}'. Valid values: smoke benchmark load all"
      OVERALL_EXIT=1; return ;;
  esac

  # ── Smoke tests (xUnit ceiling tests) ─────────────────────────────────
  if [[ "$run_smoke" == "true" ]]; then
    check_performance_prereqs || { log_error "Performance prereqs not met."; OVERALL_EXIT=1; return; }
    [[ "${OPT_NO_BUILD}" == "false" ]] && build_solution
    run_dotnet_test "MavenOperator.Tests.Performance" "Performance (smoke)" \
      "PERFORMANCE_TESTS=true"
  fi

  # ── BenchmarkDotNet micro-benchmarks ──────────────────────────────────
  if [[ "$run_bench" == "true" ]]; then
    check_performance_prereqs || { log_error "Performance prereqs not met."; OVERALL_EXIT=1; return; }
    log_step "Building performance project in Release mode..."
    dotnet build "${REPO_ROOT}/MavenOperator.Tests.Performance" --configuration Release -v minimal

    local bench_results_dir="${REPO_ROOT}/.benchmarks"
    mkdir -p "$bench_results_dir"

    log_step "Running BenchmarkDotNet benchmarks..."
    if dotnet run \
        --project "${REPO_ROOT}/MavenOperator.Tests.Performance" \
        --configuration Release \
        --no-build \
        -- --benchmark \
        --artifacts "${bench_results_dir}"; then
      log_ok "Benchmarks completed. Results in ${bench_results_dir}"
    else
      log_error "BenchmarkDotNet benchmarks FAILED."
      OVERALL_EXIT=1
    fi
  fi

  # ── k6 load tests ─────────────────────────────────────────────────────
  if [[ "$run_load" == "true" ]]; then
    check_performance_load_prereqs || { log_error "Performance load prereqs not met."; OVERALL_EXIT=1; return; }

    # Need a fully deployed cluster with operator + a test repo exposed.
    if [[ -z "${_EXTERNAL_KUBECONFIG}" ]]; then
      cluster_up
      cluster_apply_crds
      cluster_apply_rbac
      cluster_load_operator_image
      cluster_load_virtual_proxy_image
      cluster_deploy_operator
    else
      log_info "Using external KUBECONFIG: ${KUBECONFIG}"
      log_warn "Assuming operator is already deployed in cluster."
    fi
    export KUBECONFIG

    log_step "Provisioning k6 load-test repository..."
    local load_ns="maven-perf"
    local load_name="perf-hosted"
    kubectl get namespace "$load_ns" &>/dev/null || kubectl create namespace "$load_ns"

    # Apply a simple MavenRepository for the load test (if not already present).
    kubectl apply -f - --server-side <<EOF
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepository
metadata:
  name: ${load_name}
  namespace: ${load_ns}
spec:
  type: Hosted
  storage:
    size: 1Gi
    deletionPolicy: Delete
  auth:
    download:
      policy: Anonymous
    upload:
      policy: Anonymous
EOF

    log_step "Waiting for ${load_name}-svc to appear..."
    local deadline
    deadline=$(( $(date +%s) + 120 ))
    until kubectl get svc "${load_name}-svc" -n "$load_ns" &>/dev/null; do
      [[ $(date +%s) -gt $deadline ]] && { log_error "Timed out waiting for ${load_name}-svc"; OVERALL_EXIT=1; return; }
      sleep 3
    done
    log_ok "Service ${load_name}-svc is ready."

    # Wait for the NGINX pod to become Ready before port-forwarding.
    log_step "Waiting for ${load_name} NGINX pod to become Ready..."
    deadline=$(( $(date +%s) + 180 ))
    until kubectl wait pod \
        --for=condition=Ready \
        -l "app=${load_name}-nginx" \
        -n "$load_ns" \
        --timeout=10s &>/dev/null 2>&1; do
      [[ $(date +%s) -gt $deadline ]] && {
        log_error "Timed out waiting for ${load_name}-nginx pod to be Ready."
        kubectl get pods -n "$load_ns" -l "app=${load_name}-nginx" 2>/dev/null || true
        OVERALL_EXIT=1; return
      }
      sleep 5
    done
    log_ok "${load_name} NGINX pod is Ready."

    # Start port-forward and wait until the local port actually accepts connections.
    local load_port=18080
    # Kill any stale port-forward from a previous run on this port (idempotent).
    fuser -k "${load_port}/tcp" &>/dev/null || true

    mkdir -p "${REPO_ROOT}/.tmp"
    log_step "Starting port-forward svc/${load_name}-svc -> localhost:${load_port}..."
    kubectl port-forward "svc/${load_name}-svc" "${load_port}:80" -n "$load_ns" \
      >"${REPO_ROOT}/.tmp/pf-${load_name}.log" 2>&1 &
    local pf_pid=$!
    trap "kill $pf_pid 2>/dev/null || true" RETURN

    # Poll until TCP connect succeeds — proves kubectl tunnel is actually forwarding.
    local pf_deadline=$(( $(date +%s) + 30 ))
    until bash -c "echo >/dev/tcp/127.0.0.1/${load_port}" 2>/dev/null; do
      if [[ $(date +%s) -gt $pf_deadline ]]; then
        log_error "Port-forward to localhost:${load_port} never became reachable."
        cat "${REPO_ROOT}/.tmp/pf-${load_name}.log" 2>/dev/null || true
        kill $pf_pid 2>/dev/null || true
        OVERALL_EXIT=1; return
      fi
      if ! kill -0 $pf_pid 2>/dev/null; then
        log_error "Port-forward process exited unexpectedly."
        cat "${REPO_ROOT}/.tmp/pf-${load_name}.log" 2>/dev/null || true
        OVERALL_EXIT=1; return
      fi
      sleep 1
    done
    log_ok "Port-forward is alive on localhost:${load_port}."

    local k6_dir="${REPO_ROOT}/MavenOperator.Tests.Performance/k6"
    local k6_out_dir="${REPO_ROOT}/.benchmarks"
    mkdir -p "$k6_out_dir"

    log_step "Running k6 general load test (hosted-load-test.js)..."
    if REPO_URL="http://localhost:${load_port}/repository/${load_name}" \
       k6 run "${k6_dir}/hosted-load-test.js" \
         -e REPO_URL="http://localhost:${load_port}/repository/${load_name}" \
         -e AUTH_DOWNLOAD="anon:anon" \
         -e AUTH_UPLOAD="anon:anon" \
         --out "json=${k6_out_dir}/k6-load-summary.json"; then
      log_ok "k6 general load test PASSED. Summary: ${k6_out_dir}/k6-load-summary.json"
    else
      log_error "k6 general load test FAILED (threshold breached or error)."
      OVERALL_EXIT=1
    fi

    log_step "Running k6 concurrent download stress test (concurrent-download-test.js)..."
    if k6 run "${k6_dir}/concurrent-download-test.js" \
         -e REPO_URL="http://localhost:${load_port}/repository/${load_name}" \
         -e AUTH_DOWNLOAD="anon:anon" \
         -e AUTH_UPLOAD="anon:anon" \
         -e CATALOGUE_SIZE="30" \
         -e ARTIFACT_SIZE_BYTES="51200" \
         -e HOT_VUS="120" \
         -e CATALOGUE_VUS="100" \
         --out "json=${k6_out_dir}/k6-concurrent-summary.json"; then
      log_ok "k6 concurrent download test PASSED. Summary: ${k6_out_dir}/k6-concurrent-summary.json"
    else
      log_error "k6 concurrent download test FAILED (threshold breached or error)."
      OVERALL_EXIT=1
    fi
  fi
}

# ═════════════════════════════════════════════════════════════════════════════
# SUITE: cleanup
# Tears down the k3d cluster and deletes all operator-managed namespaces.
# Safe to run standalone: ./scripts/run-tests.sh cleanup
# ═════════════════════════════════════════════════════════════════════════════
run_cleanup() {
  log_section "Suite: cleanup"

  # Always enable cluster deletion for this suite.
  export K3D_CLEANUP=true

  # Delete the k3d cluster (delegates to cluster_down which checks K3D_CLEANUP).
  cluster_down

  # Also remove the operator namespace if kubectl is available and we have a kubeconfig.
  if command -v kubectl &>/dev/null; then
    local kc="${REPO_ROOT}/.tmp/kubeconfig-${K3D_CLUSTER_NAME}.yaml"
    # Try the default kubeconfig path first, then fall back to KUBECONFIG env.
    local kubeconfig_path="${KUBECONFIG:-${kc}}"
    if [[ -f "$kubeconfig_path" ]]; then
      local ns="${OPERATOR_NAMESPACE}"
      if KUBECONFIG="$kubeconfig_path" kubectl get namespace "$ns" &>/dev/null 2>&1; then
        log_step "Deleting operator namespace '${ns}'..."
        KUBECONFIG="$kubeconfig_path" kubectl delete namespace "$ns" --ignore-not-found &>/dev/null || true
        log_ok "Namespace '${ns}' deleted."
      else
        log_info "Operator namespace '${ns}' does not exist — nothing to delete."
      fi
    fi
  fi

  # Clean up the tmp kubeconfig.
  local kc="${REPO_ROOT}/.tmp/kubeconfig-${K3D_CLUSTER_NAME}.yaml"
  if [[ -f "$kc" ]]; then
    rm -f "$kc"
    log_ok "Removed kubeconfig ${kc}"
  fi

  log_ok "Cleanup complete."
}

# ── Main ──────────────────────────────────────────────────────────────────────
log_section "MavenOperator test runner"
log_info "Suites   : ${SUITES[*]}"
log_info "Repo root: ${REPO_ROOT}"
log_info "Cluster  : ${K3D_CLUSTER_NAME}  |  Namespace: ${OPERATOR_NAMESPACE}"
log_info "Image    : ${OPERATOR_IMAGE}"
log_info "Proxy    : ${VIRTUAL_PROXY_IMAGE}"
log_info "Perf mode: ${PERF_MODE}"
echo

for suite in "${SUITES[@]}"; do
  case "$suite" in
    unit)        run_unit ;;
    integration) run_integration ;;
    e2e)         run_e2e ;;
    performance) run_performance ;;
    cleanup)     run_cleanup ;;
  esac
done

# ── Summary ───────────────────────────────────────────────────────────────────
echo
if [[ "$OVERALL_EXIT" -eq 0 ]]; then
  log_ok "All requested test suites PASSED. ✓"
else
  log_error "One or more test suites FAILED. ✗"
fi

exit "$OVERALL_EXIT"




