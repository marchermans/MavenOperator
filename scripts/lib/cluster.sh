#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# lib/cluster.sh — k3d cluster lifecycle helpers
# Source this file; do not execute it directly.
#
# Exported variables after cluster_up:
#   K3D_CLUSTER_NAME   — name of the k3d cluster
#   KUBECONFIG         — path to the generated kubeconfig
# ─────────────────────────────────────────────────────────────────────────────

K3D_CLUSTER_NAME="${K3D_CLUSTER_NAME:-maven-operator-test}"
_K3D_KUBECONFIG="${REPO_ROOT}/.tmp/kubeconfig-${K3D_CLUSTER_NAME}.yaml"

# Create the k3d cluster (idempotent — skip if already running).
cluster_up() {
  log_section "k3d cluster"

  if k3d cluster list 2>/dev/null | grep -q "^${K3D_CLUSTER_NAME}\b"; then
    log_info "Cluster '${K3D_CLUSTER_NAME}' already exists — reusing."
  else
    log_step "Creating k3d cluster '${K3D_CLUSTER_NAME}'…"
    k3d cluster create "${K3D_CLUSTER_NAME}" \
      --servers 1 \
      --agents 1 \
      --wait \
      --timeout 120s \
      --k3s-arg '--disable=traefik@server:0' \
      --no-lb
    log_ok "Cluster '${K3D_CLUSTER_NAME}' created."
  fi

  mkdir -p "$(dirname "$_K3D_KUBECONFIG")"
  k3d kubeconfig get "${K3D_CLUSTER_NAME}" > "$_K3D_KUBECONFIG"
  export KUBECONFIG="$_K3D_KUBECONFIG"
  log_ok "KUBECONFIG → $KUBECONFIG"

  # Wait for the node(s) to become Ready
  log_step "Waiting for nodes to be Ready…"
  kubectl wait node --all --for=condition=Ready --timeout=90s
  log_ok "All nodes Ready."
}

# Tear down the k3d cluster (called on EXIT when --cleanup is set).
cluster_down() {
  if [[ "${K3D_CLEANUP:-false}" == "true" ]]; then
    log_section "Tearing down k3d cluster"
    k3d cluster delete "${K3D_CLUSTER_NAME}" 2>/dev/null || true
    rm -f "$_K3D_KUBECONFIG"
    log_ok "Cluster '${K3D_CLUSTER_NAME}' deleted."
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

# Build the virtual-proxy container image and import it into k3d.
# Sets VIRTUAL_PROXY_IMAGE_IN_CLUSTER similar to OPERATOR_IMAGE_IN_CLUSTER.
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

  log_step "Loading image into k3d cluster '${K3D_CLUSTER_NAME}'..."
  local _tmp_tar
  _tmp_tar="$(mktemp --suffix=.tar)"
  "${CONTAINER_RUNTIME}" save -o "${_tmp_tar}" "${image_tag}"
  k3d image import "${_tmp_tar}" --cluster "${K3D_CLUSTER_NAME}"
  rm -f "${_tmp_tar}"
  log_ok "Virtual-proxy image '${image_tag}' loaded into cluster."

  # Determine the in-cluster ref (podman stores under localhost/).
  local server_node="k3d-${K3D_CLUSTER_NAME}-server-0"
  local short_name tag_part crictl_out matched
  short_name="${image_tag%%:*}"
  tag_part="${image_tag#*:}"
  crictl_out=$(docker exec "${server_node}" crictl images 2>/dev/null \
    || podman exec "${server_node}" crictl images 2>/dev/null \
    || true)
  matched=$(printf '%s\n' "${crictl_out}" \
    | awk -v name="${short_name}" -v tag="${tag_part}" \
        '$1 ~ name && $2 == tag { print $1 ":" $2; exit }')
  if [[ -n "${matched}" ]]; then
    VIRTUAL_PROXY_IMAGE_IN_CLUSTER="${matched}"
  else
    VIRTUAL_PROXY_IMAGE_IN_CLUSTER="localhost/${image_tag}"
  fi
  export VIRTUAL_PROXY_IMAGE_IN_CLUSTER
  log_info "Virtual-proxy in-cluster image ref: ${VIRTUAL_PROXY_IMAGE_IN_CLUSTER}"

  # Re-tag under canonical docker.io/library/ if needed.
  if [[ "${VIRTUAL_PROXY_IMAGE_IN_CLUSTER}" == localhost/* ]]; then
    local canonical_tag="docker.io/library/${image_tag}"
    log_step "Re-tagging virtual-proxy as canonical '${canonical_tag}'..."
    "${CONTAINER_RUNTIME}" tag "${image_tag}" "${canonical_tag}" 2>/dev/null || true
    local _tmp_tar2
    _tmp_tar2="$(mktemp --suffix=.tar)"
    "${CONTAINER_RUNTIME}" save -o "${_tmp_tar2}" "${canonical_tag}"
    k3d image import "${_tmp_tar2}" --cluster "${K3D_CLUSTER_NAME}"
    rm -f "${_tmp_tar2}"
    log_ok "Virtual-proxy canonical image tag also loaded."
  fi

  # Sync localhost/ tag on all k3d nodes.
  log_step "Syncing virtual-proxy localhost/ tag on all k3d nodes..."
  for _node in "k3d-${K3D_CLUSTER_NAME}-server-0" "k3d-${K3D_CLUSTER_NAME}-agent-0"; do
    local _canonical="docker.io/library/${image_tag}"
    docker exec "${_node}" ctr images remove "localhost/${image_tag}" 2>/dev/null || true
    docker exec "${_node}" ctr images tag "${_canonical}" "localhost/${image_tag}" 2>/dev/null || true
  done
  log_ok "Virtual-proxy localhost/ tag synced."
}

# Build the operator container image and import it into k3d.
# Sets OPERATOR_IMAGE_IN_CLUSTER to the exact image ref that containerd inside
# the k3d nodes knows about (which may differ from OPERATOR_IMAGE when using
# podman, because podman-built images are stored under the "localhost/" registry).
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
  log_step "Loading image into k3d cluster '${K3D_CLUSTER_NAME}'..."
  # Export to a tar and import into k3d so both Docker and podman work.
  local _tmp_tar=""
  _tmp_tar="$(mktemp --suffix=.tar)"
  "${CONTAINER_RUNTIME}" save -o "${_tmp_tar}" "${image_tag}"
  k3d image import "${_tmp_tar}" --cluster "${K3D_CLUSTER_NAME}"
  rm -f "${_tmp_tar}"
  log_ok "Image '${image_tag}' loaded into cluster."
  # Determine the image ref as containerd inside k3d sees it.
  # When using podman the image is stored under "localhost/<name>:<tag>".
  # We inspect crictl on the server node to find the exact ref.
  local server_node="k3d-${K3D_CLUSTER_NAME}-server-0"
  local short_name tag_part crictl_out matched
  short_name="${image_tag%%:*}"
  tag_part="${image_tag#*:}"
  crictl_out=$(docker exec "${server_node}" crictl images 2>/dev/null \
    || podman exec "${server_node}" crictl images 2>/dev/null \
    || true)
  matched=$(printf '%s\n' "${crictl_out}" \
    | awk -v name="${short_name}" -v tag="${tag_part}" \
        '$1 ~ name && $2 == tag { print $1 ":" $2; exit }')
  if [[ -n "${matched}" ]]; then
    OPERATOR_IMAGE_IN_CLUSTER="${matched}"
  else
    # Fallback: assume localhost/ prefix (default for podman)
    OPERATOR_IMAGE_IN_CLUSTER="localhost/${image_tag}"
  fi
  export OPERATOR_IMAGE_IN_CLUSTER
  log_info "In-cluster image ref: ${OPERATOR_IMAGE_IN_CLUSTER}"

  # If the image was imported under "localhost/" prefix but the deployment uses
  # "docker.io/library/" prefix, also import under that canonical name so
  # rollout restarts pick up the correct image.
  if [[ "${OPERATOR_IMAGE_IN_CLUSTER}" == localhost/* ]]; then
    local canonical_tag="docker.io/library/${image_tag}"
    log_step "Re-tagging as canonical '${canonical_tag}' for deployment compatibility..."
    "${CONTAINER_RUNTIME}" tag "${image_tag}" "${canonical_tag}" 2>/dev/null || true
    local _tmp_tar2
    _tmp_tar2="$(mktemp --suffix=.tar)"
    "${CONTAINER_RUNTIME}" save -o "${_tmp_tar2}" "${canonical_tag}"
    k3d image import "${_tmp_tar2}" --cluster "${K3D_CLUSTER_NAME}"
    rm -f "${_tmp_tar2}"
    log_ok "Canonical image tag also loaded."
  fi

  # Ensure localhost/ tag is also updated to the new digest on all k3d nodes
  # (containerd won't overwrite an existing tag on import — must re-tag explicitly).
  log_step "Syncing localhost/ tag on all k3d nodes..."
  for _node in "k3d-${K3D_CLUSTER_NAME}-server-0" "k3d-${K3D_CLUSTER_NAME}-agent-0"; do
    local _canonical="docker.io/library/${image_tag}"
    docker exec "${_node}" ctr images remove "localhost/${image_tag}" 2>/dev/null || true
    docker exec "${_node}" ctr images tag "${_canonical}" "localhost/${image_tag}" 2>/dev/null || true
  done
  log_ok "localhost/ tag synced."
}

# Deploy the operator into the cluster using a minimal dev manifest.
cluster_deploy_operator() {
  # Use the in-cluster ref computed by cluster_load_operator_image (which
  # accounts for the "localhost/" prefix that podman-based imports get).
  local image_tag="${OPERATOR_IMAGE_IN_CLUSTER:-localhost/${OPERATOR_IMAGE:-maven-operator:dev}}"
  local virtual_proxy_image="${VIRTUAL_PROXY_IMAGE_IN_CLUSTER:-localhost/${VIRTUAL_PROXY_IMAGE:-maven-virtual-proxy:dev}}"
  local namespace="${OPERATOR_NAMESPACE:-maven-operator-system}"

  log_section "Deploying operator"

  kubectl get namespace "$namespace" &>/dev/null || \
    kubectl create namespace "$namespace"

  # Write a minimal operator Deployment and wait for it to roll out.
  kubectl apply -f - --server-side --force-conflicts <<EOF
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
              value: ${virtual_proxy_image}
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
}

