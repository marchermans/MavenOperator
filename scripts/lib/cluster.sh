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
K3D_AGENTS="${K3D_AGENTS:-2}"

# Max pods per node — raised from the k3s default of 110 to give extra headroom
# when all agents land on a small VM/CI runner with a single agent.
K3D_MAX_PODS="${K3D_MAX_PODS:-250}"

# Create the k3d cluster (idempotent — skip if already running).
cluster_up() {
  log_section "k3d cluster"

  if k3d cluster list 2>/dev/null | grep -q "^${K3D_CLUSTER_NAME}\b"; then
    log_info "Cluster '${K3D_CLUSTER_NAME}' already exists — reusing."
  else
    log_step "Creating k3d cluster '${K3D_CLUSTER_NAME}' (agents=${K3D_AGENTS}, max-pods=${K3D_MAX_PODS})…"
    k3d cluster create "${K3D_CLUSTER_NAME}" \
      --servers 1 \
      --agents "${K3D_AGENTS}" \
      --wait \
      --timeout 300s \
      --k3s-arg "--disable=traefik@server:0" \
      --k3s-arg "--disable=metrics-server@server:0" \
      --k3s-arg "--kubelet-arg=max-pods=${K3D_MAX_PODS}@server:*" \
      --k3s-arg "--kubelet-arg=max-pods=${K3D_MAX_PODS}@agent:*" \
      --k3s-arg "--kube-apiserver-arg=max-requests-inflight=800@server:0" \
      --k3s-arg "--kube-apiserver-arg=max-mutating-requests-inflight=400@server:0" \
      --no-lb
    log_ok "Cluster '${K3D_CLUSTER_NAME}' created."
  fi

  mkdir -p "$(dirname "$_K3D_KUBECONFIG")"
  k3d kubeconfig get "${K3D_CLUSTER_NAME}" > "$_K3D_KUBECONFIG"
  export KUBECONFIG="$_K3D_KUBECONFIG"
  log_ok "KUBECONFIG → $KUBECONFIG"

  # k3d --wait already blocks until the cluster is ready; add a short grace
  # period poll only to handle the rare race where kubeconfig is written
  # before the API server makes the node Ready.
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

  # Pre-load sidecar images now that the cluster is confirmed healthy.
  # This is a best-effort step — failure is non-fatal (pods will pull from
  # the internet on first use, just slower).
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

  log_section "Pre-loading sidecar images into k3d"

  local _tmp_tar=""
  for img in "${SIDECAR_IMAGES[@]}"; do
    # Check if the image is already present in the cluster's containerd
    # (skip re-import on re-runs to keep startup fast).
    local _server_node="k3d-${K3D_CLUSTER_NAME}-server-0"
    local _short="${img##*/}"         # e.g. nginx:1.27-alpine
    local _short_name="${_short%%:*}" # e.g. nginx
    local _short_tag="${_short##*:}"  # e.g. 1.27-alpine
    # k3d nodes are always docker containers — use docker to exec into them
    if docker exec "${_server_node}" crictl images 2>/dev/null \
        | awk -v name="${_short_name}" -v tag="${_short_tag}" \
            '$1 ~ name && $2 == tag { found=1 } END { exit !found }'; then
      log_info "  ${img} already in containerd — skipping."
      continue
    fi

    log_step "Pulling and importing '${img}'…"
    if "${CONTAINER_RUNTIME}" pull "${img}"; then
      _tmp_tar="$(mktemp --suffix=.tar)"
      "${CONTAINER_RUNTIME}" save -o "${_tmp_tar}" "${img}"
      k3d image import "${_tmp_tar}" --cluster "${K3D_CLUSTER_NAME}"
      rm -f "${_tmp_tar}"
      log_ok "  ${img} imported."
    else
      log_warn "  Could not pull '${img}' — sidecar pods may pull from internet on first use."
    fi
  done
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

  log_step "Loading virtual-proxy into all k3d nodes directly via containerd..."
  local _tmp_tar_vp
  _tmp_tar_vp="$(mktemp --suffix=.tar)"
  _container_save "${image_tag}" "${_tmp_tar_vp}"

  local _all_nodes_vp
  # k3d node list does not support --cluster; filter by cluster column in awk
  _all_nodes_vp=$(k3d node list --no-headers 2>/dev/null | awk -v cluster="${K3D_CLUSTER_NAME}" '$3 == cluster && !/tools/ {print $1}')
  local _canonical_vp="docker.io/library/${image_tag}"
  # k3d nodes are always Docker containers — always use docker for node operations,
  # regardless of which runtime (podman/docker) was used to build the image.
  for _node in ${_all_nodes_vp}; do
    docker exec "${_node}" ctr images remove "localhost/${image_tag}" 2>/dev/null || true
    docker exec "${_node}" ctr images remove "${_canonical_vp}" 2>/dev/null || true
    docker cp "${_tmp_tar_vp}" "${_node}:/tmp/_k8s_vp_import.tar"
    docker exec "${_node}" ctr images import "/tmp/_k8s_vp_import.tar"
    docker exec "${_node}" rm -f "/tmp/_k8s_vp_import.tar" 2>/dev/null
    if docker exec "${_node}" ctr images ls 2>/dev/null | grep -q "localhost/${image_tag}"; then
      docker exec "${_node}" ctr images tag "localhost/${image_tag}" "${_canonical_vp}" 2>/dev/null || true
    fi
    log_info "  Loaded virtual-proxy on ${_node}"
  done
  rm -f "${_tmp_tar_vp}"
  log_ok "Virtual-proxy image '${image_tag}' loaded into all cluster nodes."

  VIRTUAL_PROXY_IMAGE_IN_CLUSTER="${_canonical_vp}"
  export VIRTUAL_PROXY_IMAGE_IN_CLUSTER
  log_info "Virtual-proxy in-cluster image ref: ${VIRTUAL_PROXY_IMAGE_IN_CLUSTER}"
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

  log_step "Loading image into all k3d nodes directly via containerd..."
  # Export to a tar once, then import directly into each node's containerd via
  # 'docker cp + ctr images import'. This is more reliable than 'k3d image import'
  # because containerd's import will create a new tag pointing to the new digest,
  # whereas k3d's shared-volume approach may leave stale tags on node restarts.
  # k3d nodes are always Docker containers — always use docker for node operations,
  # regardless of which runtime (podman/docker) was used to build the image.
  local _tmp_tar=""
  _tmp_tar="$(mktemp --suffix=.tar)"
  _container_save "${image_tag}" "${_tmp_tar}"

  local _all_nodes
  # k3d node list does not support --cluster; filter by cluster column in awk
  _all_nodes=$(k3d node list --no-headers 2>/dev/null | awk -v cluster="${K3D_CLUSTER_NAME}" '$3 == cluster && !/tools/ {print $1}')
  local _canonical="docker.io/library/${image_tag}"
  for _node in ${_all_nodes}; do
    # Remove stale named tags so ctr import creates a fresh one.
    docker exec "${_node}" ctr images remove "localhost/${image_tag}" 2>/dev/null || true
    docker exec "${_node}" ctr images remove "${_canonical}" 2>/dev/null || true
    # Copy tar directly into node filesystem and import.
    docker cp "${_tmp_tar}" "${_node}:/tmp/_k8s_image_import.tar"
    docker exec "${_node}" ctr images import "/tmp/_k8s_image_import.tar"
    docker exec "${_node}" rm -f "/tmp/_k8s_image_import.tar" 2>/dev/null
    # The import creates a 'localhost/<name>:<tag>' ref; also tag under docker.io/library/.
    if docker exec "${_node}" ctr images ls 2>/dev/null | grep -q "localhost/${image_tag}"; then
      docker exec "${_node}" ctr images tag "localhost/${image_tag}" "${_canonical}" 2>/dev/null || true
    fi
    log_info "  Loaded on ${_node}"
  done
  rm -f "${_tmp_tar}"
  log_ok "Image '${image_tag}' loaded into all cluster nodes."

  # Set the in-cluster ref used by cluster_deploy_operator.
  OPERATOR_IMAGE_IN_CLUSTER="${_canonical}"
  export OPERATOR_IMAGE_IN_CLUSTER
  log_info "In-cluster image ref: ${OPERATOR_IMAGE_IN_CLUSTER}"
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

  # Force a rollout restart so containerd picks up the newly-imported image.
  # Without this, if the image tag hasn't changed (e.g. always "dev"), Kubernetes
  # won't restart the pod and the old image continues to run.
  kubectl rollout restart deployment/maven-operator -n "$namespace"

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

