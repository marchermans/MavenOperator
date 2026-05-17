#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# run-k6-comparison.sh — One-command MavenOperator vs Reposilite k6 comparison
#
# What this script does:
#   1) Creates/reuses a k3d cluster.
#   2) Loads required images (operator, virtual-proxy, sidecars, reposilite).
#   3) Deploys MavenOperator + Reposilite.
#   4) Seeds both with the same benchmark artifacts.
#   5) Runs k6 comparison/compare.sh.
#   6) Copies summaries to .benchmarks and cleans up (unless --keep-cluster).
#
# Usage:
#   ./scripts/run-k6-comparison.sh
#   ./scripts/run-k6-comparison.sh --keep-cluster
#   ./scripts/run-k6-comparison.sh --cluster my-cluster --reposilite-image ghcr.io/dzikoysk/reposilite:latest
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
export REPO_ROOT

# shellcheck source=lib/log.sh
source "${SCRIPT_DIR}/lib/log.sh"
# shellcheck source=lib/checks.sh
source "${SCRIPT_DIR}/lib/checks.sh"

K3D_CLUSTER_NAME="${K3D_CLUSTER_NAME:-maven-operator-compare}"
OPERATOR_NAMESPACE="${OPERATOR_NAMESPACE:-maven-operator-system}"
OPERATOR_IMAGE="${OPERATOR_IMAGE:-maven-operator:dev}"
VIRTUAL_PROXY_IMAGE="${VIRTUAL_PROXY_IMAGE:-maven-virtual-proxy:dev}"
REPOSILITE_IMAGE="${REPOSILITE_IMAGE:-ghcr.io/dzikoysk/reposilite:latest}"

COMPARE_NAMESPACE="${COMPARE_NAMESPACE:-maven-compare}"
MAVEN_REPO_NAME="${MAVEN_REPO_NAME:-releases}"
REPOSILITE_NAME="${REPOSILITE_NAME:-reposilite}"

LOCAL_OPERATOR_PORT="${LOCAL_OPERATOR_PORT:-18081}"
LOCAL_REPOSILITE_PORT="${LOCAL_REPOSILITE_PORT:-18082}"

DOWNLOAD_USER="${DOWNLOAD_USER:-anon}"
DOWNLOAD_PASS="${DOWNLOAD_PASS:-anon}"

KEEP_CLUSTER=false

usage() {
  cat <<EOF
Usage: ./scripts/run-k6-comparison.sh [options]

Options:
  --cluster <name>           k3d cluster name (default: ${K3D_CLUSTER_NAME})
  --namespace <name>         operator namespace (default: ${OPERATOR_NAMESPACE})
  --image <tag>              operator image tag (default: ${OPERATOR_IMAGE})
  --proxy-image <tag>        virtual-proxy image tag (default: ${VIRTUAL_PROXY_IMAGE})
  --reposilite-image <tag>   reposilite image tag (default: ${REPOSILITE_IMAGE})
  --keep-cluster             do not delete the k3d cluster at the end
  -h, --help                 show this help

Environment overrides:
  DOWNLOAD_USER, DOWNLOAD_PASS, LOCAL_OPERATOR_PORT, LOCAL_REPOSILITE_PORT,
  COMPARE_NAMESPACE, MAVEN_REPO_NAME, REPOSILITE_NAME
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cluster)
      K3D_CLUSTER_NAME="$2"; shift 2 ;;
    --namespace)
      OPERATOR_NAMESPACE="$2"; shift 2 ;;
    --image)
      OPERATOR_IMAGE="$2"; shift 2 ;;
    --proxy-image)
      VIRTUAL_PROXY_IMAGE="$2"; shift 2 ;;
    --reposilite-image)
      REPOSILITE_IMAGE="$2"; shift 2 ;;
    --keep-cluster)
      KEEP_CLUSTER=true; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      log_error "Unknown argument: $1"
      usage
      exit 1 ;;
  esac
done

export K3D_CLUSTER_NAME OPERATOR_NAMESPACE OPERATOR_IMAGE VIRTUAL_PROXY_IMAGE
# shellcheck source=lib/cluster.sh
source "${SCRIPT_DIR}/lib/cluster.sh"

COMPARE_DIR="${REPO_ROOT}/MavenOperator.Tests.Performance/k6/comparison"
BENCHMARK_DIR="${REPO_ROOT}/.benchmarks"

PF_OPERATOR_PID=""
PF_REPOSILITE_PID=""

cleanup() {
  if [[ -n "${PF_OPERATOR_PID}" ]]; then
    kill "${PF_OPERATOR_PID}" 2>/dev/null || true
  fi
  if [[ -n "${PF_REPOSILITE_PID}" ]]; then
    kill "${PF_REPOSILITE_PID}" 2>/dev/null || true
  fi

  if [[ "${KEEP_CLUSTER}" == "true" ]]; then
    log_info "Keeping cluster '${K3D_CLUSTER_NAME}' (--keep-cluster)."
  else
    export K3D_CLEANUP=true
    cluster_down || true
  fi
}
trap cleanup EXIT

wait_for_service() {
  local namespace="$1"
  local service="$2"
  local timeout_seconds="${3:-180}"
  local deadline=$(( $(date +%s) + timeout_seconds ))

  until kubectl get service "${service}" -n "${namespace}" &>/dev/null; do
    if [[ $(date +%s) -gt ${deadline} ]]; then
      log_error "Timed out waiting for service ${namespace}/${service}"
      return 1
    fi
    sleep 2
  done
}

wait_for_pod_ready() {
  local namespace="$1"
  local selector="$2"
  local timeout_seconds="${3:-240}"
  local deadline=$(( $(date +%s) + timeout_seconds ))

  until kubectl wait pod -n "${namespace}" -l "${selector}" --for=condition=Ready --timeout=10s &>/dev/null; do
    if [[ $(date +%s) -gt ${deadline} ]]; then
      log_error "Timed out waiting for pods in ${namespace} with selector '${selector}'"
      kubectl get pods -n "${namespace}" -l "${selector}" || true
      return 1
    fi
    sleep 3
  done
}

start_port_forward() {
  local namespace="$1"
  local service="$2"
  local local_port="$3"
  local remote_port="$4"
  local log_file="$5"

  fuser -k "${local_port}/tcp" &>/dev/null || true

  kubectl port-forward -n "${namespace}" "svc/${service}" "${local_port}:${remote_port}" >"${log_file}" 2>&1 &
  local pf_pid=$!

  local deadline=$(( $(date +%s) + 40 ))
  until bash -c "echo >/dev/tcp/127.0.0.1/${local_port}" 2>/dev/null; do
    if [[ $(date +%s) -gt ${deadline} ]]; then
      log_error "Port-forward for svc/${service} on ${local_port} did not become reachable"
      cat "${log_file}" || true
      kill "${pf_pid}" 2>/dev/null || true
      return 1
    fi
    if ! kill -0 "${pf_pid}" 2>/dev/null; then
      log_error "Port-forward process for svc/${service} exited unexpectedly"
      cat "${log_file}" || true
      return 1
    fi
    sleep 1
  done

  echo "${pf_pid}"
}

upload_file() {
  local url="$1"
  local file_path="$2"

  curl --fail --silent --show-error \
    -X PUT \
    -H "Content-Type: application/octet-stream" \
    --data-binary "@${file_path}" \
    "${url}" >/dev/null
}

seed_targets() {
  local operator_base_url="$1"
  local reposilite_base_url="$2"
  local repository_name="$3"

  local tmp_dir
  tmp_dir="$(mktemp -d)"
  trap 'rm -rf "${tmp_dir}"' RETURN

  local small_jar="${tmp_dir}/benchmark-small-1.0.jar"
  local large_jar="${tmp_dir}/benchmark-large-1.0.jar"
  local metadata_xml="${tmp_dir}/maven-metadata.xml"

  head -c 51200 </dev/zero >"${small_jar}"
  head -c 1048576 </dev/zero >"${large_jar}"

  cat >"${metadata_xml}" <<EOF
<metadata>
  <groupId>com.example</groupId>
  <artifactId>benchmark-small</artifactId>
  <versioning>
    <latest>1.0</latest>
    <release>1.0</release>
    <versions>
      <version>1.0</version>
    </versions>
    <lastUpdated>20260517000000</lastUpdated>
  </versioning>
</metadata>
EOF

  log_step "Seeding MavenOperator repository artifacts..."
  upload_file "${operator_base_url}/repository/${repository_name}/com/example/benchmark-small/1.0/benchmark-small-1.0.jar" "${small_jar}"
  upload_file "${operator_base_url}/repository/${repository_name}/com/example/benchmark-large/1.0/benchmark-large-1.0.jar" "${large_jar}"
  upload_file "${operator_base_url}/repository/${repository_name}/com/example/benchmark-small/maven-metadata.xml" "${metadata_xml}"

  log_step "Seeding Reposilite repository artifacts (filesystem copy)..."
  local reposilite_pod
  reposilite_pod="$(kubectl get pods -n "${COMPARE_NAMESPACE}" -l "app=${REPOSILITE_NAME}" -o jsonpath='{.items[0].metadata.name}')"
  if [[ -z "${reposilite_pod}" ]]; then
    log_error "Could not locate Reposilite pod in namespace ${COMPARE_NAMESPACE}."
    return 1
  fi

  local target_dir="/app/data/repositories/${repository_name}/com/example"
  kubectl exec -n "${COMPARE_NAMESPACE}" "${reposilite_pod}" -- sh -c \
    "mkdir -p ${target_dir}/benchmark-small/1.0 ${target_dir}/benchmark-large/1.0"

  kubectl cp "${small_jar}" "${COMPARE_NAMESPACE}/${reposilite_pod}:${target_dir}/benchmark-small/1.0/benchmark-small-1.0.jar"
  kubectl cp "${large_jar}" "${COMPARE_NAMESPACE}/${reposilite_pod}:${target_dir}/benchmark-large/1.0/benchmark-large-1.0.jar"
  kubectl cp "${metadata_xml}" "${COMPARE_NAMESPACE}/${reposilite_pod}:${target_dir}/benchmark-small/maven-metadata.xml"
}

log_section "k6 comparison runner"
log_info "Cluster    : ${K3D_CLUSTER_NAME}"
log_info "Namespace  : ${COMPARE_NAMESPACE}"
log_info "Operator   : ${OPERATOR_IMAGE}"
log_info "VProxy     : ${VIRTUAL_PROXY_IMAGE}"
log_info "Reposilite : ${REPOSILITE_IMAGE}"

check_performance_load_prereqs
require_tool curl "https://curl.se/"
require_tool python3 "https://www.python.org/"

cluster_up
cluster_apply_crds
cluster_apply_rbac
cluster_load_operator_image
cluster_load_virtual_proxy_image

log_step "Pulling/importing Reposilite image into k3d..."
"${CONTAINER_RUNTIME}" pull "${REPOSILITE_IMAGE}"
# Import via tar so podman-managed images are visible to k3d regardless of daemon wiring.
_reposilite_tar="$(mktemp --suffix=.tar)"
_container_save "${REPOSILITE_IMAGE}" "${_reposilite_tar}"
k3d image import "${_reposilite_tar}" -c "${K3D_CLUSTER_NAME}"
rm -f "${_reposilite_tar}"

cluster_deploy_operator

kubectl get namespace "${COMPARE_NAMESPACE}" &>/dev/null || kubectl create namespace "${COMPARE_NAMESPACE}"

log_step "Deploying Reposilite..."
kubectl apply -n "${COMPARE_NAMESPACE}" -f - <<EOF
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ${REPOSILITE_NAME}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ${REPOSILITE_NAME}
  template:
    metadata:
      labels:
        app: ${REPOSILITE_NAME}
    spec:
      containers:
        - name: reposilite
          image: ${REPOSILITE_IMAGE}
          imagePullPolicy: IfNotPresent
          ports:
            - containerPort: 8080
              name: http
          readinessProbe:
            tcpSocket:
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 5
          livenessProbe:
            tcpSocket:
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 10
---
apiVersion: v1
kind: Service
metadata:
  name: ${REPOSILITE_NAME}
spec:
  selector:
    app: ${REPOSILITE_NAME}
  ports:
    - name: http
      port: 8080
      targetPort: 8080
EOF

log_step "Deploying MavenOperator hosted repository for comparison..."
kubectl apply -n "${COMPARE_NAMESPACE}" -f - <<EOF
apiVersion: maven.operator.io/v1alpha1
kind: MavenRepository
metadata:
  name: ${MAVEN_REPO_NAME}
spec:
  type: Hosted
  storage:
    size: 1Gi
    accessMode: ReadWriteOnce
    deletionPolicy: Delete
  resources:
    requests:
      cpu: "500m"
      memory: "512Mi"
    limits:
      cpu: "2"
      memory: "1Gi"
  auth:
    download:
      policy: Anonymous
    upload:
      policy: Anonymous
EOF

wait_for_service "${COMPARE_NAMESPACE}" "${REPOSILITE_NAME}" 120
wait_for_service "${COMPARE_NAMESPACE}" "${MAVEN_REPO_NAME}-svc" 180
wait_for_pod_ready "${COMPARE_NAMESPACE}" "app=${REPOSILITE_NAME}" 180
wait_for_pod_ready "${COMPARE_NAMESPACE}" "app=${MAVEN_REPO_NAME}-nginx" 240

mkdir -p "${REPO_ROOT}/.tmp"
PF_OPERATOR_PID="$(start_port_forward "${COMPARE_NAMESPACE}" "${MAVEN_REPO_NAME}-svc" "${LOCAL_OPERATOR_PORT}" "80" "${REPO_ROOT}/.tmp/pf-${MAVEN_REPO_NAME}.log")"
PF_REPOSILITE_PID="$(start_port_forward "${COMPARE_NAMESPACE}" "${REPOSILITE_NAME}" "${LOCAL_REPOSILITE_PORT}" "8080" "${REPO_ROOT}/.tmp/pf-${REPOSILITE_NAME}.log")"

OPERATOR_BASE_URL="http://127.0.0.1:${LOCAL_OPERATOR_PORT}"
REPOSILITE_BASE_URL="http://127.0.0.1:${LOCAL_REPOSILITE_PORT}"

seed_targets "${OPERATOR_BASE_URL}" "${REPOSILITE_BASE_URL}" "${MAVEN_REPO_NAME}"

log_section "Running k6 comparison"
pushd "${COMPARE_DIR}" >/dev/null
set +e
./compare.sh \
  --maven-operator-url "${OPERATOR_BASE_URL}" \
  --reposilite-url "${REPOSILITE_BASE_URL}" \
  --repository "${MAVEN_REPO_NAME}" \
  --parallel-scenarios false \
  --download-user "${DOWNLOAD_USER}" \
  --download-pass "${DOWNLOAD_PASS}"
COMPARE_EXIT=$?
set -e

mkdir -p "${BENCHMARK_DIR}"
run_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_dir="${BENCHMARK_DIR}/k6-comparison-${run_stamp}"
mkdir -p "${run_dir}"
cp -f summary-maven-operator.json summary-reposilite.json summary.json "${run_dir}/"
cp -f raw-maven-operator.json raw-reposilite.json "${run_dir}/" 2>/dev/null || true
popd >/dev/null

log_ok "Comparison artifacts saved to ${run_dir}"

if [[ ${COMPARE_EXIT} -eq 0 ]]; then
  log_ok "Comparison gates passed."
else
  log_error "Comparison gates failed (see ${run_dir}/summary.json)."
fi

exit ${COMPARE_EXIT}

