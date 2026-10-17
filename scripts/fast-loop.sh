#!/usr/bin/env bash
# =============================================================================
# Fast E2E iteration loop.
#
# Rebuilds ONLY the operator image, loads it into the k3d test cluster, force-
# redeploys the operator, cleans stale e2e namespaces, then runs a slice of the
# E2E suite. The import-job image is rebuilt + imported every run (the operator
# falls back to a nonexistent ghcr.io tag without it — Import tests hang in
# ErrImagePull otherwise). Other sidecars (virtual-proxy, metadata-broker) are
# assumed unchanged and already loaded — rebuild them via scripts/run-tests.sh
# e2e if you touched them.
#
# Usage:
#   scripts/fast-loop.sh                     # redeploy + run HostedRepository slice
#   scripts/fast-loop.sh --all               # redeploy + run the FULL E2E suite (~10 min)
#   scripts/fast-loop.sh --filter "FullyQualifiedName~ProxyUpload"
#   scripts/fast-loop.sh --no-test           # redeploy only
#
# Requires a prebuilt E2E test binary (run 'dotnet build' once, or let this
# script do it). The operator image is always rebuilt (--no-cache docker build),
# so compile errors are caught there; the quick local dotnet build below fails
# faster on syntax/type errors.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

source "${SCRIPT_DIR}/lib/log.sh"
source "${SCRIPT_DIR}/lib/checks.sh"
source "${SCRIPT_DIR}/lib/cluster.sh"
check_container_runtime   # sets + exports CONTAINER_RUNTIME (prefers podman)

# ── Defaults (mirror run-tests.sh) ───────────────────────────────────────────
export K3D_CLUSTER_NAME="${K3D_CLUSTER_NAME:-maven-operator-test}"
export OPERATOR_NAMESPACE="${OPERATOR_NAMESPACE:-maven-operator-system}"
export OPERATOR_IMAGE="${OPERATOR_IMAGE:-maven-operator:dev}"
export VIRTUAL_PROXY_IMAGE="${VIRTUAL_PROXY_IMAGE:-maven-virtual-proxy:dev}"

FILTER="FullyQualifiedName~HostedRepositoryE2ETests"
RUN_TESTS=true

while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter)   FILTER="$2"; shift 2 ;;
    --all)      FILTER="FullyQualifiedName~MavenOperator.Tests.E2E"; shift ;;
    --no-test)  RUN_TESTS=false; shift ;;
    *)          log_error "Unknown argument: $1"; exit 2 ;;
  esac
done

# ── Kubeconfig ────────────────────────────────────────────────────────────────
export KUBECONFIG="${KUBECONFIG:-${REPO_ROOT}/.tmp/kubeconfig-${K3D_CLUSTER_NAME}.yaml}"
if [[ ! -f "${KUBECONFIG}" ]]; then
  log_warn "Kubeconfig ${KUBECONFIG} missing — regenerating from k3d…"
  k3d get-kubeconfig --cluster "${K3D_CLUSTER_NAME}" > "${KUBECONFIG}"
fi
log_info "Using kubeconfig: ${KUBECONFIG}"

# ── Quick compile check before the slow docker build ──────────────────────────
log_step "Quick local compile check…"
dotnet build "${REPO_ROOT}/MavenOperator" -c Debug --nologo >/dev/null \
  || { log_error "Local compile failed — aborting before docker build."; exit 1; }

# ── Import job image: rebuild + import so the operator gets IMPORT_JOB_IMAGE ──
# (without it MavenRepositoryImport Jobs pull a nonexistent ghcr.io tag and
#  PvcSnapshotImport times out in ErrImagePull — see HANDOFF.md)
cluster_load_import_job_image

# ── Rebuild + load operator image, force redeploy ─────────────────────────────
cluster_load_operator_image
cluster_deploy_operator
log_ok "Operator redeployed with fresh instrumentation."

# ── Free CPU: drop namespaces left over from interrupted/previous runs ───────
cluster_cleanup_stale_test_namespaces || true

# The suite recreates its test namespaces on start — wait for ALL of them to be
# fully gone, or fixture setup hits 'namespace is being terminated' (Forbidden)
# / PVC create into a Terminating ns → NotFound. Covers maven-e2e AND
# maven-e2e-import (PvcSnapshotImport) and any maven-int variants.
for _ in $(seq 1 90); do
  kubectl get namespaces --no-headers 2>/dev/null | awk '{print $1}' \
    | grep -qE '^maven-(int|e2e)(-[a-z0-9]+)?$' || break
  sleep 2
done

if [[ "${RUN_TESTS}" == false ]]; then
  log_section "Done (--no-test). Start /tmp/capture-rs3.sh in the background to watch rollouts."
  exit 0
fi

# ── Run the E2E slice (test binary is built by dotnet test; --no-build only if it exists) ──
log_section "Running E2E slice: ${FILTER}"
cd "${REPO_ROOT}"
if [[ -d "${REPO_ROOT}/MavenOperator.Tests.E2E/bin/Debug" ]]; then
  BUILD_FLAG="--no-build"; else dotnet build "${REPO_ROOT}/MavenOperator.Tests.E2E" -c Debug >/dev/null; BUILD_FLAG="--no-build"; fi
local_e2e_log="/tmp/fastloop-e2e-$(date +%H%M%S).log"
E2E_TESTS=true \
IMPORT_DIRECT_WRITE_TESTS="${IMPORT_DIRECT_WRITE_TESTS:-true}" \
dotnet test "${REPO_ROOT}/MavenOperator.Tests.E2E" ${BUILD_FLAG} -c Debug \
  --filter "${FILTER}" > "${local_e2e_log}" 2>&1 || true
log_info "Full E2E output: ${local_e2e_log}"
tail -40 "${local_e2e_log}"
