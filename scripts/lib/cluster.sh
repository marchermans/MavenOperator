#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# lib/cluster.sh — Kubernetes cluster lifecycle helpers (runtime-agnostic)
#
# Detects and works with: k3d (Linux), Apple 'container' (macOS), or external
# clusters (pre-existing KUBECONFIG).
#
# Source order in run-tests.sh MUST be:
#   1. lib/log.sh        (logging helpers)
#   2. lib/checks.sh     (prerequisite checks)
#   3. lib/runtime.sh    (runtime abstraction layer)
#   4. lib/cluster.sh    (this file — cluster lifecycle)
#
# Exported variables after cluster_up:
#   K3D_CLUSTER_NAME   — name of the cluster (k3d name or container name)
#   KUBECONFIG         — path to the kubeconfig
#   CLUSTER_TYPE       — "k3d" | "container" | "external"
# ─────────────────────────────────────────────────────────────────────────────

K3D_CLUSTER_NAME="${K3D_CLUSTER_NAME:-maven-operator-test}"

# For k3d: the cluster-specific kubeconfig path (only used by k3d runtime).
_K3D_KUBECONFIG="${REPO_ROOT}/.tmp/kubeconfig-${K3D_CLUSTER_NAME}.yaml"

# Track whether we merged into the global kubeconfig so we can restore on exit.
_K3D_GLOBAL_MERGED=false
_K3D_GLOBAL_BACKUP=""

# Source the runtime abstraction layer — MUST be sourced before any other
# cluster.sh functions that use CLUSTER_TYPE.
# shellcheck source=runtime.sh
source "${SCRIPT_DIR}/lib/runtime.sh"

# Restore the original global kubeconfig if we merged into it.
_k3d_restore_global_config() {
  if [[ "${_K3D_GLOBAL_MERGED}" == "true" && -n "${_K3D_GLOBAL_BACKUP}" && -f "${_K3D_GLOBAL_BACKUP}" ]]; then
    cp "${_K3D_GLOBAL_BACKUP}" "${HOME}/.kube/config" 2>/dev/null || true
    rm -f "${_K3D_GLOBAL_BACKUP}"
    _K3D_GLOBAL_MERGED=false
    _K3D_GLOBAL_BACKUP=""
  fi
}

# Clean up stale test namespaces from previous runs to free cluster resources.
# Integration tests create maven-int-* namespaces per run; E2E uses maven-e2e.
# These accumulate over time and exhaust CPU/memory if not cleaned up.
cluster_cleanup_test_namespaces() {
  log_section "Cleaning up stale test namespaces"

  local -a stale_ns=()

  # Find all maven-int-* and maven-e2e namespaces (skip operator-system)
  while IFS= read -r ns; do
    [[ -n "$ns" ]] && stale_ns+=("$ns")
  done < <(kubectl get namespaces --no-headers -o custom-columns="NAME:.metadata.name" 2>/dev/null \
    | grep -E '^(maven-int-[a-f0-9]+|maven-e2e)$' || true)

  if [[ ${#stale_ns[@]} -eq 0 ]]; then
    log_info "No stale test namespaces found."
    return 0
  fi

  local _count=${#stale_ns[@]}
  log_step "Found ${_count} stale namespace(s), deleting in parallel…"

  # Delete all at once with --force to avoid waiting for each individually.
  kubectl delete namespaces "${stale_ns[@]}" --ignore-not-found &>/dev/null || true

  # Wait up to 120s for them to terminate (best-effort; don't block forever).
  local _deadline=$(( $(date +%s) + 120 ))
  while [[ $(date +%s) -lt $_deadline ]]; do
    local remaining=$(kubectl get namespaces --no-headers -o custom-columns="NAME:.metadata.name" 2>/dev/null \
      | grep -cE '^(maven-int-[a-f0-9]+|maven-e2e)$' || echo "0")
    [[ "$remaining" == "0" ]] && break
    sleep 5
  done

  log_ok "Stale test namespaces cleaned up."
}

# ── Cluster sizing ────────────────────────────────────────────────────────────
# The operator tests spin up multiple NGINX pods per test suite
# (each MavenRepository creates at least one pod, plus operator pod, plus
# Reposilite for import tests).  The k3s default max-pods=110 is sufficient,
# but the default node count of 1 server + 1 agent limits total schedulable
# capacity.  We use 2 agent nodes so we have headroom for:
#   - operator pod                          ~1
#   - integration tests: up to ~20 repos    ~20
#   - e2e tests: hosted + proxy + virtual   ~10
#   - import tests: Reposilite + nginx      ~10
#   - performance: perf-hosted              ~5
#   - system workloads (coredns, metrics)   ~5
#                                           ── ~50 pods, comfortable margin

# How many k3d agent nodes to provision (override via K3D_AGENTS env var).
# Single node by default: import tests need the repo NGINX pod and the import
# Job to share an RWO PVC, which is only guaranteed on a single node.  CI
# passes an explicit value; raise this locally for more scheduling headroom.
K3D_AGENTS="${K3D_AGENTS:-0}"

# Max pods per node — raised from the k3s default of 110 to give extra headroom
# when all agents land on a small VM/CI runner with a single agent.
K3D_MAX_PODS="${K3D_MAX_PODS:-250}"

# Create the cluster (idempotent — skip if already running).
# Detects k3d, Apple 'container', or external KUBECONFIG.
cluster_up() {
  log_section "Kubernetes cluster"

  # Detect runtime on first call (idempotent after first detection).
  _detect_runtime || { log_error "Cannot determine Kubernetes runtime."; return 1; }

  # Initialize the cluster (create if needed, write/point to kubeconfig).
  cluster_init

  # Merge into global kubeconfig (k3d-only; no-op for container/external).
  cluster_merge_global_kubeconfig

  log_ok "KUBECONFIG → ${KUBECONFIG:-not set}"

  # Verify all nodes are Ready.
  log_step "Verifying all nodes are Ready…"
  local _node_deadline=$(( $(date +%s) + 120 ))
  until kubectl wait node --all --for=condition=Ready --timeout=10s &>/dev/null; do
    if [[ $(date +%s) -gt $_node_deadline ]]; then
      log_error "Nodes did not become Ready within 120 s after cluster creation."
      kubectl get nodes 2>/dev/null || true
      return 1
    fi
    sleep 5
  done
  log_ok "All nodes Ready."

  # Pretty-print node capacities for visibility.
  log_info "Node capacities:"
  kubectl get nodes \
    -o custom-columns="NAME:.metadata.name,CPU:.status.capacity.cpu,MEMORY:.status.capacity.memory,MAX-PODS:.status.capacity.pods" \
    2>/dev/null || true

  # Clean up stale test namespaces from previous runs to free cluster resources.
  cluster_cleanup_test_namespaces

  # Pre-load sidecar images now that the cluster is confirmed healthy.
  cluster_preload_sidecar_images
}

# Pre-pull the nginx-prometheus-exporter and mtail images that the operator
# injects as sidecars when spec.metrics.enabled=true.  Importing them into
# containerd before any test runs prevents ImagePullBackOff / long image-pull
# delays from causing pod-readiness timeouts in tests.
#
# All image references MUST be fully-qualified (registry/image:tag).
# Short names trigger podman's interactive registry-selection prompt, which
# breaks unattended runs.
cluster_preload_sidecar_images() {
  local -a SIDECAR_IMAGES=(
    "docker.io/library/nginx:1.27-alpine"
    "docker.io/nginx/nginx-prometheus-exporter:1.4"
    "ghcr.io/google/mtail:latest"
  )

  log_section "Pre-loading sidecar images"

  # Track failures — we accumulate them to report all at once, then fail.
  local _failed_count=0
  local -a _failed_images=()

  for img in "${SIDECAR_IMAGES[@]}"; do
    # For k3d: check the server node's containerd directly.
    # For container: pre-load images into the cluster.
    # For external: skip (pods will pull from registry).

    if [[ "${CLUSTER_TYPE}" == "k3d" ]]; then
      local _server_node="k3d-${K3D_CLUSTER_NAME}-server-0"
      local _short="${img##*/}"
      local _short_name="${_short%%:*}"
      local _short_tag="${_short##*:}"

      # Check whether the image is already in containerd (skip re-import).
      # Use a variable to capture pipeline result so pipefail doesn't abort.
      local _in_containerd=false
      docker exec "${_server_node}" crictl images 2>/dev/null \
          | awk -v name="${_short_name}" -v tag="${_short_tag}" \
              '$1 ~ name && $2 == tag { found=1 } END { exit !found }' 2>/dev/null \
          && _in_containerd=true || true
      if [[ "$_in_containerd" == "true" ]]; then
        log_info "  ${img} already in containerd — skipping."
        continue
      fi
    elif [[ "${CLUSTER_TYPE}" == "container" ]]; then
      log_step "Pulling and pre-loading '${img}' into cluster…"
      if container image pull "${img}" 2>&1 | grep -q 'Unpacking'; then
        if container k8s load-image --name "${K3D_CLUSTER_NAME}" "${img}" 2>&1 \
            | grep -q 'Importing'; then
          log_ok "  ${img} pre-loaded successfully."
          continue
        else
          log_error "  Failed to load '${img}' — containerd import step failed."
        fi
      else
        log_error "  Could not pull '${img}' — container registry unreachable."
      fi
      _failed_count=$((_failed_count + 1))
      _failed_images+=("${img}")
      continue
    else
      log_info "  ${img} — will be pulled by pods from registry (no containerd pre-load for ${CLUSTER_TYPE})."
      continue
    fi

    log_step "Pulling and importing '${img}'…"
    # Attempt pull — fail the function (not just warn) if it doesn't work.
    if ! "${CONTAINER_RUNTIME}" pull "${img}"; then
      log_error "  Could not pull '${img}' from container registry."
      _failed_count=$((_failed_count + 1))
      _failed_images+=("${img}")
      continue
    fi
    # Import into k3d — save to a temp tar file (Docker format, required by
    # k3d's containerd), then import the file.  Piping to k3d image import via
    # stdin is unreliable across versions; writing to a file and passing the
    # path is the stable approach.
    _tmp_tar="$(mktemp --suffix=.tar)"
    _container_save "${img}" "${_tmp_tar}"
    if ! k3d image import -c "${K3D_CLUSTER_NAME}" "${_tmp_tar}"; then
      log_error "  Failed to import '${img}' into cluster containerd."
      rm -f "${_tmp_tar}"
      _failed_count=$((_failed_count + 1))
      _failed_images+=("${img}")
      continue
    fi
    rm -f "${_tmp_tar}"
    log_ok "  ${img} pulled and imported."
  done

  # Report and fail if any sidecar image pre-loading failed.
  if [[ "${_failed_count}" -gt 0 ]]; then
    log_error "Failed to pre-load ${_failed_count} sidecar image(s):"
    for img in "${_failed_images[@]}"; do
      echo "  - ${img}"
    done
    echo
    echo "These images are required for metrics sidecar integration tests."
    echo "Verify the cluster has internet access and the registry is reachable."
    return 1
  fi
  return 0
}

# Save a container image to a tar file in Docker format.
# containerd's 'ctr images import' requires Docker (v1.1+) format, not OCI.
# podman save defaults to OCI; docker save always uses Docker format.
# Usage: _container_save <image_tag> <output_tar>
_container_save() {
  local _img="$1" _out="$2"
  if [[ "${CONTAINER_RUNTIME}" == "podman" ]]; then
    podman save --format docker-archive -o "${_out}" "${_img}"
  else
    docker save -o "${_out}" "${_img}"
  fi
}

# Output a container image as a Docker-format tarball to stdout.
# Used by 'k3d image import' which reads the tar from stdin.
# podman save defaults to OCI format — must add --format docker-archive.
# Usage: _container_save_to_stdout <image_tag>
_container_save_to_stdout() {
  local _img="$1"
  if [[ "${CONTAINER_RUNTIME}" == "podman" ]]; then
    podman save --format docker-archive - "${_img}"
  else
    docker save - "${_img}"
  fi
}

# Delete leftover test namespaces from previously interrupted runs. On the single-node
# test cluster their running pods consume CPU and can starve newly created job/pod
# scheduling (observed: import jobs stuck in FailedScheduling for ~9 minutes).
cluster_cleanup_stale_test_namespaces() {
    local stale_ns
    stale_ns=$(kubectl get namespaces --no-headers 2>/dev/null \
        | awk '{print $1}' \
        | grep -E '^maven-(int|e2e)(-[a-z0-9]+)?$' || true)
    if [ -z "$stale_ns" ]; then
        return 0
    fi
    log_warn "Deleting stale test namespaces: $(echo $stale_ns | tr '\n' ' ')"
    for ns in $stale_ns; do
        kubectl delete namespace "$ns" --wait=false >/dev/null 2>&1 &
    done
    wait || true
}

# Tear down the cluster (called on EXIT when --cleanup is set).
cluster_down() {
  # Default CLUSTER_TYPE to "none" when cluster_up was never called
  # (e.g. when prereqs failed). This avoids 'unbound variable' errors.
  local _type="${CLUSTER_TYPE:-none}"
  if [[ "${K3D_CLEANUP:-false}" == "true" ]]; then
    log_section "Tearing down cluster (${_type})"

    # Only attempt teardown if we actually have a runtime.
    if [[ "$_type" != "none" ]]; then
      cluster_teardown
    else
      log_info "No cluster was created — nothing to tear down."
    fi
  else
    log_info "Cluster '${K3D_CLUSTER_NAME}' kept alive (pass --cleanup to delete)."
  fi
}

# Apply the operator CRDs.
# Uses the checked-in YAML under config/crds/ (source of truth).
cluster_apply_crds() {
  log_step "Applying CRDs…"
  local crd_dir="${REPO_ROOT}/config/crds"
  if [[ -d "$crd_dir" ]]; then
    kubectl apply -f "$crd_dir" --server-side
    log_ok "CRDs applied from $crd_dir"
  else
    log_error "CRD directory '$crd_dir' not found. It should be checked into the repo."
    return 1
  fi
}

# Build the virtual-proxy container image and load it into the cluster.
# Sets VIRTUAL_PROXY_IMAGE_IN_CLUSTER.
cluster_load_virtual_proxy_image() {
  local image_tag="${VIRTUAL_PROXY_IMAGE:-maven-virtual-proxy:dev}"
  log_section "Building & loading virtual-proxy image"
  log_step "Building image '${image_tag}' with ${CONTAINER_RUNTIME}..."
  "${CONTAINER_RUNTIME}" build \
    --no-cache \
    -f "${REPO_ROOT}/MavenOperator.VirtualProxy/Dockerfile" \
    -t "${image_tag}" \
    "${REPO_ROOT}"
  log_ok "Virtual-proxy image built."

  # Use the abstracted image loading (runtime-agnostic).
  # For k3d: exports to tar, imports into each node's containerd.
  # For container: uses 'container k8s load-image'.
  local _canonical
  _canonical="$(cluster_load_image "${image_tag}")"
  if [[ -z "$_canonical" ]]; then
    _canonical="docker.io/library/${image_tag}"
  fi

  VIRTUAL_PROXY_IMAGE_IN_CLUSTER="${_canonical}"
  export VIRTUAL_PROXY_IMAGE_IN_CLUSTER
  log_info "Virtual-proxy in-cluster image ref: ${VIRTUAL_PROXY_IMAGE_IN_CLUSTER}"
}

# Build the metadata-broker container image and import it into k3d.
# Sets METADATA_BROKER_IMAGE_IN_CLUSTER similar to VIRTUAL_PROXY_IMAGE_IN_CLUSTER.
cluster_load_metadata_broker_image() {
  local image_tag="${METADATA_BROKER_IMAGE:-maven-metadata-broker:dev}"
  log_section "Building & loading metadata-broker image"
  log_step "Building image '${image_tag}' with ${CONTAINER_RUNTIME}..."
  "${CONTAINER_RUNTIME}" build \
    --no-cache \
    -f "${REPO_ROOT}/MavenOperator.MetadataBroker/Dockerfile" \
    -t "${image_tag}" \
    "${REPO_ROOT}"
  log_ok "Metadata-broker image built."

  log_step "Loading metadata-broker into all k3d nodes directly via containerd..."
  local _tmp_tar_mb
  _tmp_tar_mb="$(mktemp --suffix=.tar)"
  _container_save "${image_tag}" "${_tmp_tar_mb}"

  local _all_nodes_mb
  # k3d node list does not support --cluster; filter by cluster column in awk
  _all_nodes_mb=$(k3d node list --no-headers 2>/dev/null | awk -v cluster="${K3D_CLUSTER_NAME}" '$3 == cluster && !/tools/ {print $1}')
  local _canonical_mb="docker.io/library/${image_tag}"
  # k3d nodes are always Docker containers — always use docker for node operations,
  # regardless of which runtime (podman/docker) was used to build the image.
  for _node in ${_all_nodes_mb}; do
    docker exec "${_node}" ctr images remove "localhost/${image_tag}" 2>/dev/null || true
    docker exec "${_node}" ctr images remove "${_canonical_mb}" 2>/dev/null || true
    docker cp "${_tmp_tar_mb}" "${_node}:/tmp/_k8s_mb_import.tar"
    docker exec "${_node}" ctr images import "/tmp/_k8s_mb_import.tar"
    docker exec "${_node}" rm -f "/tmp/_k8s_mb_import.tar" 2>/dev/null
    # Always create the docker.io/library tag — Kubernetes uses this ref.
    docker exec "${_node}" ctr images tag "localhost/${image_tag}" "${_canonical_mb}" 2>/dev/null || true
    log_info "  Loaded metadata-broker on ${_node}"
  done
  rm -f "${_tmp_tar_mb}"
  log_ok "Metadata-broker image '${image_tag}' loaded into all cluster nodes."

  METADATA_BROKER_IMAGE_IN_CLUSTER="${_canonical_mb}"
  export METADATA_BROKER_IMAGE_IN_CLUSTER
  log_info "Metadata-broker in-cluster image ref: ${METADATA_BROKER_IMAGE_IN_CLUSTER}"
}

# Build the operator container image and load it into the cluster.
# Sets OPERATOR_IMAGE_IN_CLUSTER to the exact image ref the cluster knows about.
cluster_load_operator_image() {
  local image_tag="${OPERATOR_IMAGE:-maven-operator:dev}"
  log_section "Building & loading operator image"
  log_step "Building image '${image_tag}' with ${CONTAINER_RUNTIME}..."
  "${CONTAINER_RUNTIME}" build \
    --no-cache \
    -f "${REPO_ROOT}/MavenOperator/Dockerfile" \
    -t "${image_tag}" \
    "${REPO_ROOT}"
  log_ok "Image built."

  # Use the abstracted image loading (runtime-agnostic).
  local _canonical
  _canonical="$(cluster_load_image "${image_tag}")"
  if [[ -z "$_canonical" ]]; then
    _canonical="docker.io/library/${image_tag}"
  fi

  OPERATOR_IMAGE_IN_CLUSTER="${_canonical}"
  export OPERATOR_IMAGE_IN_CLUSTER
  log_info "In-cluster image ref: ${OPERATOR_IMAGE_IN_CLUSTER}"
}

# ── RWX storage for import tests ───────────────────────────────────────────────
# NOTE: no in-cluster NFS/RWX StorageClass is provisioned on purpose.  Many dev
# machines and CI runners run host kernels without nfsd support, so an
# in-cluster NFS server cannot start there (the kernel module is shared with the
# Docker host).  Instead the test cluster runs single-node by default
# (K3D_AGENTS=0): ReadWriteOnce PVCs may then be mounted by both the repository
# NGINX pod and the import Job, because they are guaranteed to land on the same
# node.  Import tests that require direct-write access are gated on
# IMPORT_DIRECT_WRITE_TESTS=true, which run-tests.sh enables automatically for
# its self-managed single-node clusters.

# ── Gateway API CRDs ───────────────────────────────────────────────────────────
# Applies the standard Gateway API v1.2.0 install manifest so that
# MavenRepository.spec.gateway can create HTTPRoutes.
# Idempotent — skipped when httproutes.gateway.networking.k8s.io exists.
cluster_apply_gateway_crds() {
  if kubectl get crd httproutes.gateway.networking.k8s.io &>/dev/null; then
    log_info "Gateway API CRDs already installed — skipping."
    return 0
  fi

  local url="https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.2.0/standard-install.yaml"
  log_step "Applying Gateway API v1.2.0 standard install..."
  curl -fsSL "${url}" | kubectl apply --server-side -f - >/dev/null || {
    log_error "Failed to apply Gateway API manifests from ${url} (no internet?)."
    exit 1
  }
  log_ok "Gateway API CRDs installed."
}

# ── Import Job image (Phase 7) ───────────────────────────────────────────────
# Builds MavenOperator.ImportJob/Dockerfile and loads it into the cluster.
# Exports IMPORT_JOB_IMAGE_IN_CLUSTER so tests/operator pick up the in-cluster
# reference instead of the ghcr.io default baked into the controller.
cluster_load_import_job_image() {
  local image_tag="${IMPORT_JOB_IMAGE:-maven-import-job:dev}"
  log_section "Building & loading import job image"
  log_step "Building image '${image_tag}' with ${CONTAINER_RUNTIME}..."
  if ! "${CONTAINER_RUNTIME}" build \
      --no-cache \
      -f "${REPO_ROOT}/MavenOperator.ImportJob/Dockerfile" \
      -t "${image_tag}" \
      "${REPO_ROOT}"; then
    log_error "Import job image build failed."
    exit 1
  fi
  log_ok "Import job image built."

  # Use the abstracted image loading (runtime-agnostic).
  local _canonical
  _canonical="$(cluster_load_image "${image_tag}")"
  if [[ -z "$_canonical" ]]; then
    _canonical="docker.io/library/${image_tag}"
  fi

  IMPORT_JOB_IMAGE_IN_CLUSTER="${_canonical}"
  export IMPORT_JOB_IMAGE_IN_CLUSTER
  log_info "In-cluster image ref: ${IMPORT_JOB_IMAGE_IN_CLUSTER}"
}

# ── Envoy Gateway (Gateway API data plane for E2E tests) ─────────────────────
# Installs the official Envoy Gateway helm chart.  Its bundled 'crds' subchart
# is enabled by default, so this also installs the Gateway API CRDs.
# The per-route Service it creates is a LoadBalancer that stays Pending on k3d
# (--no-lb), but E2E tests reach repositories via 'kubectl port-forward', which
# does not require a working load balancer.
cluster_install_envoy_gateway() {
  if kubectl get namespace envoy-gateway &>/dev/null; then
    log_info "Envoy Gateway already installed — skipping."
    return 0
  fi

  # NOTE: pinned below 1.9.x — the XBackend CRD introduced there uses CEL
  # `format.dns1123Label()`, which k3s v1.31's API server cannot compile.
  # Bump this together with the cluster's Kubernetes version (>= ~1.34).
  local eg_version="${ENVOY_GATEWAY_VERSION:-1.8.5}"

  # Fresh start for Gateway API CRDs: standalone manifests previously applied by
  # cluster_apply_gateway_crds may carry server-side state incompatible with the
  # chart's bundled versions (e.g. stale status.storedVersions). The chart
  # re-applies its own complete set, so drop whatever is there first.
  local crd
  for crd in $(kubectl get crd -o name 2>/dev/null \
      | grep -E 'gateway\.networking\.(k8s|x-k8s)\.io' | sed 's/^crd\///'); do
    kubectl delete "$crd" --wait=false >/dev/null 2>&1 || true
  done

  log_step "Installing Envoy Gateway helm chart v${eg_version}..."
  # --force-conflicts: allow the chart's bundled Gateway API CRDs to override
  # standalone manifests that cluster_apply_gateway_crds may have installed.
  if ! helm install gateway-helm \
      oci://registry-1.docker.io/envoyproxy/gateway-helm \
      --version "${eg_version}" \
      -n envoy-gateway --create-namespace \
      --force-conflicts >/dev/null; then
    log_error "Envoy Gateway helm install failed."
    exit 1
  fi

  log_step "Waiting for Envoy Gateway controller to be Available (120s)..."
  # The chart's controller Deployment is named after the app, not the release.
  if ! kubectl wait --for=condition=Available deployment/envoy-gateway -n envoy-gateway --timeout=120s >/dev/null; then
    log_error "Envoy Gateway controller did not become Available."
    kubectl get pods,events -n envoy-gateway 2>/dev/null || true
    exit 1
  fi

  # The GatewayClass *object* vanishes together with its CRD — the fresh-start
  # step above (and any dirty-cluster cleanup) deletes all gateway.networking.*
  # CRDs, which wipes it. Without an accepted GatewayClass every Gateway stays
  # at PROGRAMMED=False and no per-Gateway data plane is ever created.
  if ! kubectl get gatewayclass envoy-gateway &>/dev/null; then
    log_step "Creating missing GatewayClass/envoy-gateway..."
    cat <<'EOF' | kubectl apply -f - >/dev/null
apiVersion: gateway.networking.k8s.io/v1
kind: GatewayClass
metadata:
  name: envoy-gateway
spec:
  controllerName: gateway.envoyproxy.io/gatewayclass-controller
EOF
  fi
  local i
  for i in $(seq 1 30); do
    if [ "$(kubectl get gatewayclass envoy-gateway -o jsonpath='{.status.conditions[?(@.type=="Accepted")].status}' 2>/dev/null)" = "True" ]; then
      break
    fi
    sleep 2
  done
  kubectl get gatewayclass envoy-gateway -o jsonpath='{.status.conditions[?(@.type=="Accepted")].status}' >/dev/null 2>&1
  if [ "$?" -ne 0 ] || [ "$(kubectl get gatewayclass envoy-gateway -o jsonpath='{.status.conditions[?(@.type=="Accepted")].status}' 2>/dev/null)" != "True" ]; then
    log_warn "GatewayClass/envoy-gateway is not Accepted yet — gateway E2E tests may fail."
  fi

  log_ok "Envoy Gateway installed (namespace: envoy-gateway)."
}

# Deploy the operator into the cluster using a minimal dev manifest.
cluster_deploy_operator() {
  # Use the in-cluster ref computed by cluster_load_operator_image (which
  # accounts for the "localhost/" prefix that podman-based imports get).
  local image_tag="${OPERATOR_IMAGE_IN_CLUSTER:-localhost/${OPERATOR_IMAGE:-maven-operator:dev}}"
  local virtual_proxy_image="${VIRTUAL_PROXY_IMAGE_IN_CLUSTER:-localhost/${VIRTUAL_PROXY_IMAGE:-maven-virtual-proxy:dev}}"
  local metadata_broker_image="${METADATA_BROKER_IMAGE_IN_CLUSTER:-localhost/${METADATA_BROKER_IMAGE:-maven-metadata-broker:dev}}"
  local namespace="${OPERATOR_NAMESPACE:-maven-operator-system}"

  # Phase 7 — pass the in-cluster import job image ref to the operator so the
  # MavenRepositoryImport controller spawns Jobs with it (instead of pulling
  # the ghcr.io default, which does not exist on this cluster).
  local import_job_env=""
  if [[ -n "${IMPORT_JOB_IMAGE_IN_CLUSTER:-}" ]]; then
    import_job_env=$'\n            - name: IMPORT_JOB_IMAGE\n              value: '
    import_job_env+="${IMPORT_JOB_IMAGE_IN_CLUSTER}"
  fi

  log_section "Deploying operator"

  kubectl get namespace "$namespace" &>/dev/null || \
    kubectl create namespace "$namespace"

  # Force-delete any existing deployment to avoid ErrImageNeverPull on rollout restart.
  # When using imagePullPolicy: Never with a fixed tag (e.g. "dev"), Kubernetes may
  # schedule new pods on nodes where containerd has stale tags pointing to old digests.
  # Deleting the deployment entirely ensures fresh scheduling with clean state.
  if kubectl get deployment maven-operator -n "$namespace" &>/dev/null; then
    log_step "Removing existing operator deployment…"
    kubectl delete deployment maven-operator -n "$namespace" --force --grace-period=0 &>/dev/null || true
    sleep 2
  fi

  # Write a minimal operator Deployment and wait for it to roll out.
  kubectl apply -f - --server-side <<EOF
apiVersion: apps/v1
kind: Deployment
metadata:
  name: maven-operator
  namespace: ${namespace}
  labels:
    app: maven-operator
spec:
  replicas: 1
  selector:
    matchLabels:
      app: maven-operator
  template:
    metadata:
      labels:
        app: maven-operator
    spec:
      serviceAccountName: maven-operator
      containers:
        - name: operator
          image: ${image_tag}
          imagePullPolicy: Never
          env:
            - name: ASPNETCORE_ENVIRONMENT
              value: Production
            - name: OPERATOR_IMAGE
              value: ${image_tag}
            - name: VIRTUAL_PROXY_IMAGE
              value: ${virtual_proxy_image}${import_job_env}
            - name: METADATA_BROKER_IMAGE
              value: ${metadata_broker_image}
EOF

  log_step "Waiting for operator Deployment to be available…"
  kubectl rollout status deployment/maven-operator \
    -n "$namespace" --timeout=120s
  log_ok "Operator deployed and running."
}

# Apply the operator's RBAC (ServiceAccount, ClusterRole, ClusterRoleBinding).
cluster_apply_rbac() {
  local namespace="${OPERATOR_NAMESPACE:-maven-operator-system}"
  local rbac_dir="${REPO_ROOT}/MavenOperator/bin/Debug/net10.0/rbac"

  log_step "Applying RBAC…"
  if [[ -d "$rbac_dir" ]]; then
    kubectl apply -n "$namespace" -f "$rbac_dir" --server-side
    log_ok "RBAC applied from $rbac_dir"
  else
    # Fallback: create a permissive SA for dev testing
    log_warn "RBAC directory not found — creating permissive dev SA."
    kubectl get namespace "$namespace" &>/dev/null || kubectl create namespace "$namespace"
    kubectl create serviceaccount maven-operator -n "$namespace" --dry-run=client -o yaml | \
      kubectl apply -f - --server-side
    kubectl create clusterrolebinding maven-operator-admin \
      --clusterrole=cluster-admin \
      --serviceaccount="${namespace}:maven-operator" \
      --dry-run=client -o yaml | kubectl apply -f - --server-side
    log_ok "Permissive dev RBAC created."
  fi

  # Phase 7 — import Job ServiceAccount
  # The import Job needs permission to scale Deployments (Mode C), list PVCs,
  # and patch MavenRepositoryImport CRs for progress reporting.
  log_step "Applying import-job ServiceAccount and RBAC…"
  kubectl apply -f - --server-side <<EOF
apiVersion: v1
kind: ServiceAccount
metadata:
  name: maven-operator-import
  namespace: ${namespace}
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: maven-operator-import
rules:
  - apiGroups: ["apps"]
    resources: ["deployments", "deployments/scale"]
    verbs: ["get", "list", "watch", "update", "patch"]
  # Progress reporting: patch this Job's own annotations with counters
  - apiGroups: ["batch"]
    resources: ["jobs"]
    verbs: ["get", "patch"]
  - apiGroups: [""]
    resources: ["persistentvolumeclaims"]
    verbs: ["get", "list", "watch"]
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["get", "list", "watch"]
  - apiGroups: ["maven.operator.io"]
    resources: ["mavenrepositoryimports", "mavenrepositoryimports/status"]
    verbs: ["get", "list", "watch", "update", "patch"]
  - apiGroups: [""]
    resources: ["events"]
    verbs: ["create", "patch"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: maven-operator-import
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: maven-operator-import
subjects:
  - kind: ServiceAccount
    name: maven-operator-import
    namespace: ${namespace}
EOF
  log_ok "Import-job RBAC applied."
}
