#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# lib/runtime.sh — Kubernetes runtime abstraction layer
#
# Detects which Kubernetes runtime is available (k3d, Apple `container`, or
# external cluster via KUBECONFIG) and exports a set of runtime-agnostic
# functions that scripts/lib/cluster.sh calls.
#
# Source this file; do not execute it directly.
#
# After sourcing:
#   CLUSTER_TYPE   — "k3d" | "container" | "external" | "none"
#   CLUSTER_UP     — whether a cluster is already running
#
# Functions (all scripts/lib/cluster.sh functions must call these):
#   cluster_is_up()            — returns 0 if any cluster is running
#   cluster_init()             — creates a cluster (k3d or container)
#   cluster_get_nodes()        — prints node names (one per line)
#   cluster_get_kubeconfig()   — prints path to kubeconfig file
#   cluster_load_image(<tag>)  — loads a built image into the cluster
#   cluster_teardown()         — deletes the cluster
#   cluster_merge_global_kubeconfig() — merges into ~/.kube/config (no-op for container)
# ─────────────────────────────────────────────────────────────────────────────

# ── Runtime detection (called once by cluster_up) ─────────────────────────────

_detect_runtime() {
  # Determine which Kubernetes runtime to use.
  # Priority: 1) existing external KUBECONFIG  2) k3d  3) container  4) none
  if [[ -n "${_EXTERNAL_KUBECONFIG}" || -n "${KUBECONFIG:-}" ]] && \
     [[ -f "${KUBECONFIG:-}" ]]; then
    CLUSTER_TYPE="external"
    log_info "Using existing cluster (external KUBECONFIG)"
  elif command -v k3d &>/dev/null; then
    CLUSTER_TYPE="k3d"
    log_info "k3d runtime detected."
  elif command -v container &>/dev/null; then
    CLUSTER_TYPE="container"
    log_info "Apple 'container' tool runtime detected."
  else
    CLUSTER_TYPE="none"
    log_error "No Kubernetes runtime found. Install 'k3d' (Linux) or 'container' (macOS)."
    return 1
  fi
  export CLUSTER_TYPE
}

# ── Helper: determine cluster name for the runtime ────────────────────────────
_cluster_name() {
  echo "${K3D_CLUSTER_NAME:-maven-operator-test}"
}

# ── cluster_is_up: returns 0 if a cluster matching our name exists ────────────

cluster_is_up() {
  case "${CLUSTER_TYPE:-}" in
    k3d)
      k3d cluster list 2>/dev/null | grep -q "^${K3D_CLUSTER_NAME:-maven-operator-test}\b"
      ;;
    container)
      container k8s list 2>/dev/null | grep -qE "(^| )${K3D_CLUSTER_NAME:-maven-operator-test}( |$)"
      ;;
    external)
      # External is "up" if KUBECONFIG points somewhere reachable.
      [[ -f "${KUBECONFIG:-}" ]] && kubectl cluster-info &>/dev/null 2>&1
      ;;
    *)
      return 1
      ;;
  esac
}

# ── cluster_init: create the cluster (if not already up) ──────────────────────

cluster_init() {
  log_section "Initializing Kubernetes cluster (${CLUSTER_TYPE})"

  local _name
  _name="$(_cluster_name)"

  case "${CLUSTER_TYPE}" in
    k3d)
      _k3d_init_cluster "$_name"
      ;;
    container)
      _container_init_cluster "$_name"
      ;;
    external)
      log_info "External cluster — skipping creation."
      ;;
  esac
}

# ── k3d cluster creation ─────────────────────────────────────────────────────

_k3d_init_cluster() {
  local _name="$1"

  if cluster_is_up; then
    log_info "Cluster '${_name}' already exists — reusing."
  else
    log_step "Creating k3d cluster '${_name}' (agents=${K3D_AGENTS:-0}, max-pods=${K3D_MAX_PODS:-250})…"
    k3d cluster create "${_name}" \
      --servers 1 \
      --agents "${K3D_AGENTS:-0}" \
      --wait \
      --timeout 300s \
      --k3s-arg "--disable=traefik@server:0" \
      --k3s-arg "--disable=metrics-server@server:0" \
      --k3s-arg "--kubelet-arg=max-pods=${K3D_MAX_PODS:-250}@server:0" \
      --k3s-arg "--kubelet-arg=max-pods=${K3D_MAX_PODS:-250}@agent:*" \
      --k3s-arg "--kube-apiserver-arg=max-requests-inflight=800@server:0" \
      --k3s-arg "--kube-apiserver-arg=max-mutating-requests-inflight=400@server:0" \
      --no-lb
    log_ok "Cluster '${_name}' created."
  fi

  # k3d writes a dedicated kubeconfig.
  mkdir -p "$(dirname "$_K3D_KUBECONFIG")"
  k3d kubeconfig get "${_name}" > "$_K3D_KUBECONFIG"
  export KUBECONFIG="$_K3D_KUBECONFIG"
}

# ── container (Apple) cluster creation ─────────────────────────────────────────

_container_init_cluster() {
  local _name="$1"

  if cluster_is_up; then
    log_info "Cluster '${_name}' already exists — reusing."
  else
    # Determine resource allocation for the Mac: use 2 CPUs, 4GB RAM by default.
    # These are reasonable defaults that work on most M-series Macs.
    # Users can override via K8S_CPUS / K8S_MEMORY env vars.
    local _cpus="${K8S_CPUS:-2}"
    local _memory="${K8S_MEMORY:-4g}"

    # Convert K3D_AGENTS (0 = single-node) to container's --workers.
    # container uses --workers > 0 for multi-node; single node = default.
    local _workers="${K3D_AGENTS:-0}"

    log_step "Creating cluster '${_name}' (cpus=${_cpus}, memory=${_memory}, workers=${_workers})…"
    if [[ "$_workers" -gt 0 ]]; then
      container k8s create \
        --name "${_name}" \
        --cpus "${_cpus}" \
        --memory "${_memory}" \
        --workers "${_workers}"
    else
      container k8s create \
        --name "${_name}" \
        --cpus "${_cpus}" \
        --memory "${_memory}"
    fi
    log_ok "Cluster '${_name}' created."
  fi

  # The container tool auto-registers the cluster in ~/.kube/config.
  # We just need to point KUBECONFIG at it.
  local _kc="${HOME}/.kube/config"
  if [[ -f "$_kc" ]]; then
    export KUBECONFIG="$_kc"
    log_info "kubeconfig from 'container' tool: ${KUBECONFIG}"
  else
    log_warn "~/.kube/config not found — creating cluster may have failed."
    return 1
  fi
}

# ── cluster_get_nodes: print node names ───────────────────────────────────────

cluster_get_nodes() {
  case "${CLUSTER_TYPE:-}" in
    k3d)
      k3d node list --no-headers 2>/dev/null | awk -v cluster="${K3D_CLUSTER_NAME:-maven-operator-test}" \
        '$3 == cluster && !/tools/ {print $1}'
      ;;
    container)
      # The container tool manages kubeconfig, so standard kubectl works.
      kubectl --context "${K3D_CLUSTER_NAME:-maven-operator-test}" get nodes \
        -o name 2>/dev/null | sed 's|node/||'
      ;;
    external)
      kubectl get nodes -o name 2>/dev/null | sed 's|node/||'
      ;;
  esac
}

# ── cluster_get_kubeconfig: print path to kubeconfig ──────────────────────────

cluster_get_kubeconfig() {
  echo "${KUBECONFIG:-}"
}

# ── cluster_load_image: load a built container image into the cluster ─────────
#
# For k3d: exports to Docker-format tar, then imports into each node's
# containerd via docker cp + ctr images import (legacy but reliable).
#
# For container (Apple): uses 'container k8s load-image' which handles
# image loading directly — no tar export or containerd CLI needed.

cluster_load_image() {
  local _image_tag="${1:?cluster_load_image: <image_tag> required}"

  log_step "Loading image '${_image_tag}' into cluster…"

  case "${CLUSTER_TYPE:-}" in
    k3d)
      _k3d_load_image "${_image_tag}"
      ;;
    container)
      _container_load_image "${_image_tag}"
      ;;
    external)
      # For external clusters, images must already be in a reachable registry.
      log_warn "External cluster: image '${_image_tag}' must be in a reachable registry (no image injection)."
      log_info "Set imagePullPolicy: IfNotPresent or use a registry-accessible image."
      ;;
  esac
}

# ── k3d image loading (Docker-format tar + containerd) ────────────────────────

_k3d_load_image() {
  local _image_tag="$1"
  local _tmp_tar=""

  # Export to a Docker-format tar (once), then import into each node's containerd.
  _tmp_tar="$(mktemp --suffix=.tar)"

  # Use CONTAINER_RUNTIME (set by checks.sh) to save — podman or docker.
  _container_save "${_image_tag}" "${_tmp_tar}"

  local _all_nodes
  _all_nodes=$(cluster_get_nodes)

  if [[ -z "$_all_nodes" ]]; then
    log_error "Could not determine cluster nodes for image loading."
    rm -f "${_tmp_tar}"
    return 1
  fi

  local _canonical="docker.io/library/${_image_tag}"

  for _node in ${_all_nodes}; do
    # Remove stale named tags so ctr import creates a fresh one.
    docker exec "${_node}" ctr images remove "localhost/${_image_tag}" 2>/dev/null || true
    docker exec "${_node}" ctr images remove "${_canonical}" 2>/dev/null || true

    # Copy tar directly into node filesystem and import.
    docker cp "${_tmp_tar}" "${_node}:/tmp/_k8s_image_import.tar"
    docker exec "${_node}" ctr images import "/tmp/_k8s_image_import.tar"
    docker exec "${_node}" rm -f "/tmp/_k8s_image_import.tar" 2>/dev/null

    # Always create the docker.io/library tag — Kubernetes uses this ref.
    docker exec "${_node}" ctr images tag "localhost/${_image_tag}" "${_canonical}" 2>/dev/null || true
    log_info "  Loaded on ${_node}"
  done

  rm -f "${_tmp_tar}"
  log_ok "Image '${_image_tag}' loaded into all cluster nodes."
  echo "${_canonical}"  # Print canonical ref for caller to capture.
}

# ── container (Apple) image loading ────────────────────────────────────────────
#
# Simple: 'container k8s load-image' handles everything — no tar export,
# no containerd CLI, no per-node operations. Multi-arch is supported.

_container_load_image() {
  local _image_tag="$1"

  # Check architecture if needed (e.g., Mac arm64 loading into an amd64 cluster).
  local _platform=""
  # Detect the cluster's architecture from its nodes.
  local _arch
  _arch=$(kubectl --context "${K3D_CLUSTER_NAME:-maven-operator-test}" get nodes \
    -o jsonpath='{.items[0].status.nodeInfo.architecture}' 2>/dev/null || echo "arm64")

  if [[ "${_arch}" == "amd64" ]]; then
    _platform="--platform linux/amd64"
  fi

  # 'container k8s load-image' loads the image into the cluster's containerd.
  # It automatically handles the image namespace (k8s.io) and imagePullPolicy.
  if container k8s load-image ${_platform} --name "${K3D_CLUSTER_NAME:-maven-operator-test}" "${_image_tag}" 2>/dev/null; then
    log_ok "Image '${_image_tag}' loaded into cluster '${K3D_CLUSTER_NAME:-maven-operator-test}'."
  else
    log_warn "Could not load '${_image_tag}' via 'container k8s load-image'. "
    log_warn "Make sure the image was built for the cluster's architecture."
    # Return the image tag as-is; Kubernetes may still pull it from a registry.
    echo "docker.io/library/${_image_tag}"
  fi
}

# ── cluster_teardown: delete the cluster ──────────────────────────────────────

cluster_teardown() {
  [[ "${K3D_CLEANUP:-false}" != "true" ]] && {
    log_info "Cluster '${K3D_CLUSTER_NAME:-maven-operator-test}' kept alive (pass --cleanup to delete)."
    return 0
  }

  log_section "Tearing down cluster (${CLUSTER_TYPE})"

  case "${CLUSTER_TYPE}" in
    k3d)
      _k3d_teardown
      ;;
    container)
      _container_teardown
      ;;
    external)
      log_info "External cluster — not torn down by test runner."
      ;;
  esac
}

# ── k3d teardown ──────────────────────────────────────────────────────────────

_k3d_teardown() {
  local _name
  _name="$(_cluster_name)"

  # Restore global kubeconfig before deleting cluster so we don't leave stale contexts.
  _k3d_restore_global_config

  k3d cluster delete "${_name}" 2>/dev/null || true
  rm -f "$_K3D_KUBECONFIG"
  log_ok "Cluster '${_name}' deleted."
}

# ── container (Apple) teardown ─────────────────────────────────────────────────

_container_teardown() {
  local _name
  _name="$(_cluster_name)"

  container k8s delete --name "${_name}" 2>/dev/null || true
  log_ok "Cluster '${_name}' deleted."
}

# ── Global kubeconfig merge (k3d-specific) ─────────────────────────────────────

cluster_merge_global_kubeconfig() {
  case "${CLUSTER_TYPE}" in
    k3d)
      _k3d_merge_global_kubeconfig
      ;;
    container|external)
      log_info "No global kubeconfig merge needed for ${CLUSTER_TYPE} runtime."
      ;;
  esac
}

# ── k3d-specific: merge into ~/.kube/config (restore on EXIT) ─────────────────

_k3d_merge_global_kubeconfig() {
  if [[ -f "${HOME}/.kube/config" ]]; then
    _K3D_GLOBAL_BACKUP="${HOME}/.kube/config.k3d-backup-$(date +%s)"
    cp "${HOME}/.kube/config" "$_K3D_GLOBAL_BACKUP"

    local _ctx_name="k3d-${K3D_CLUSTER_NAME:-maven-operator-test}"

    if command -v python3 &>/dev/null; then
      python3 << PYEOF || true
import yaml, base64, tempfile, os, subprocess

k3d_kc = "$_K3D_KUBECONFIG"
global_kc = "${HOME}/.kube/config"
ctx_name = "$_ctx_name"

with open(k3d_kc) as f:
    k3d_cfg = yaml.safe_load(f)

cluster = k3d_cfg["clusters"][0]
user = k3d_cfg["users"][0]
cluster_name = cluster["name"]
server = cluster["cluster"]["server"]
user_name = user["name"]

# Write CA cert to temp file
ca_data = cluster["cluster"].get("certificate-authority-data", "")
ca_file = tempfile.mktemp()
with open(ca_file, "wb") as f:
    f.write(base64.b64decode(ca_data))

# Set cluster in global config
subprocess.run([
    "kubectl", "--kubeconfig", global_kc, "config", "set-cluster", cluster_name,
    "--server", server, "--certificate-authority", ca_file, "--embed-certs=true"
], capture_output=True)
os.unlink(ca_file)

# Set credentials in global config (handle both token and cert auth)
user_cfg = user["user"]
if "token" in user_cfg:
    subprocess.run([
        "kubectl", "--kubeconfig", global_kc, "config", "set-credentials", user_name,
        "--token", user_cfg["token"]
    ], capture_output=True)
elif "client-certificate-data" in user_cfg and "client-key-data" in user_cfg:
    cert_file = tempfile.mktemp()
    key_file = tempfile.mktemp()
    with open(cert_file, "wb") as f:
        f.write(base64.b64decode(user_cfg["client-certificate-data"]))
    with open(key_file, "wb") as f:
        f.write(base64.b64decode(user_cfg["client-key-data"]))

    subprocess.run([
        "kubectl", "--kubeconfig", global_kc, "config", "set-credentials", user_name,
        "--client-certificate", cert_file, "--client-key", key_file, "--embed-certs=true"
    ], capture_output=True)
    os.unlink(cert_file)
    os.unlink(key_file)

# Create and switch to context
subprocess.run([
    "kubectl", "--kubeconfig", global_kc, "config", "set-context", ctx_name,
    "--cluster", cluster_name, "--user", user_name
], capture_output=True)
subprocess.run([
    "kubectl", "--kubeconfig", global_kc, "config", "use-context", ctx_name
], capture_output=True)

print(f"Merged k3d cluster into global kubeconfig, switched to context: {ctx_name}")
PYEOF

      # Check if Python script succeeded by verifying context was set
      local current_ctx=$(kubectl --kubeconfig "${HOME}/.kube/config" config current-context 2>/dev/null || true)
      if [[ "$current_ctx" == "$_ctx_name" ]]; then
        _K3D_GLOBAL_MERGED=true
        log_ok "Merged into global kubeconfig, switched to context: ${_ctx_name}"
      else
        log_warn "Failed to merge into global kubeconfig (python script did not set context)."
      fi
    fi

    # Register restore on EXIT (only if not already registered).
    trap '_k3d_restore_global_config' EXIT
  fi
}
