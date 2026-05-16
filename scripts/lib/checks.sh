#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# lib/checks.sh — Prerequisite / tooling checks
# Source this file; do not execute it directly.
# ─────────────────────────────────────────────────────────────────────────────

# Abort if a required binary is not on PATH.
# Usage: require_tool <name> [<install-hint>]
require_tool() {
  local tool="$1"
  local hint="${2:-}"
  if ! command -v "$tool" &>/dev/null; then
    log_error "Required tool '$tool' not found on PATH."
    [[ -n "$hint" ]] && log_error "  Install hint: $hint"
    return 1
  fi
  log_ok "$tool found: $(command -v "$tool")"
}

# Check that a minimum version string is satisfied.
# Usage: require_min_version <tool> <actual_version> <min_version>
#   version strings may be "x.y.z" or "x.y"
require_min_version() {
  local tool="$1" actual="$2" min="$3"
  if [[ "$(printf '%s\n%s\n' "$min" "$actual" | sort -V | head -1)" != "$min" ]]; then
    log_error "$tool version $actual is below the required minimum $min."
    return 1
  fi
}

# ── Individual checks ─────────────────────────────────────────────────────────

check_dotnet() {
  require_tool dotnet "https://dot.net" || return 1
  local ver
  ver=$(dotnet --version 2>/dev/null)
  log_info ".NET SDK version: $ver"
  # Require .NET 10+
  local major="${ver%%.*}"
  if [[ "$major" -lt 10 ]]; then
    log_error ".NET 10 or above is required (found $ver)."
    return 1
  fi
  log_ok ".NET SDK OK"
}

check_container_runtime() {
  # Prefer podman, fall back to docker
  if command -v podman &>/dev/null; then
    CONTAINER_RUNTIME="podman"
    log_ok "Container runtime: podman ($(podman --version 2>/dev/null | head -1))"
  elif command -v docker &>/dev/null; then
    CONTAINER_RUNTIME="docker"
    log_ok "Container runtime: docker ($(docker --version 2>/dev/null))"
  else
    log_error "Neither 'podman' nor 'docker' was found on PATH."
    log_error "  Install hint: https://podman.io  or  https://docs.docker.com/get-docker/"
    return 1
  fi
  export CONTAINER_RUNTIME
}

check_kubectl() {
  require_tool kubectl "https://kubernetes.io/docs/tasks/tools/" || return 1
  local ver
  ver=$(kubectl version --client -o json 2>/dev/null | grep '"gitVersion"' | head -1 | tr -d '",' | awk '{print $2}')
  log_info "kubectl client version: $ver"
  log_ok "kubectl OK"
}

check_k3d() {
  if ! command -v k3d &>/dev/null; then
    log_warn "'k3d' not found — attempting to install via the official installer script..."
    if curl -s https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh | bash; then
      log_ok "k3d installed successfully"
    else
      log_error "Failed to install k3d. Install manually: https://k3d.io/#installation"
      return 1
    fi
  fi
  log_ok "k3d found: $(k3d version 2>/dev/null | head -1)"
}

check_helm() {
  require_tool helm "brew install helm  OR  https://helm.sh/docs/intro/install/" || return 1
  log_ok "helm OK: $(helm version --short 2>/dev/null)"
}

check_java() {
  # Maven fixture requires JDK 21+; check JAVA_HOME or PATH
  local java_bin
  if [[ -n "${JAVA_HOME:-}" ]] && [[ -x "$JAVA_HOME/bin/java" ]]; then
    java_bin="$JAVA_HOME/bin/java"
  elif command -v java &>/dev/null; then
    java_bin="java"
  else
    log_error "'java' not found. Set JAVA_HOME or add java to PATH."
    log_error "  Install hint: https://adoptium.net"
    return 1
  fi
  local ver
  ver=$("$java_bin" -version 2>&1 | head -1)
  log_ok "java OK: $ver"
}

check_k6() {
  if ! command -v k6 &>/dev/null; then
    log_warn "'k6' not found — attempting to install via the official package..."
    # Try common package managers; fall back to a helpful message.
    if command -v dnf &>/dev/null; then
      if ! rpm -q k6-repo &>/dev/null 2>&1; then
        sudo dnf install -y "https://dl.k6.io/rpm/repo.rpm" &>/dev/null || true
      fi
      sudo dnf install -y k6 &>/dev/null && log_ok "k6 installed via dnf" && return 0
    elif command -v apt-get &>/dev/null; then
      sudo gpg -k 2>/dev/null; sudo apt-key del k6 2>/dev/null || true
      curl -sL https://dl.k6.io/key.gpg | sudo apt-key add - &>/dev/null || true
      echo "deb https://dl.k6.io/deb stable main" | sudo tee /etc/apt/sources.list.d/k6.list &>/dev/null
      sudo apt-get update -qq && sudo apt-get install -y k6 &>/dev/null && log_ok "k6 installed via apt" && return 0
    elif command -v brew &>/dev/null; then
      brew install k6 && log_ok "k6 installed via brew" && return 0
    fi
    log_error "'k6' not found and could not be installed automatically."
    log_error "  Install hint: https://k6.io/docs/get-started/installation/"
    return 1
  fi
  log_ok "k6 found: $(k6 version 2>/dev/null | head -1)"
}

# ── Composite check groups ────────────────────────────────────────────────────

check_unit_prereqs() {
  log_section "Unit test prerequisites"
  check_dotnet
}

check_integration_prereqs() {
  log_section "Integration test prerequisites"
  local ok=0
  check_dotnet    || ok=1
  check_kubectl   || ok=1
  check_k3d       || ok=1
  check_container_runtime || ok=1
  return $ok
}

check_e2e_prereqs() {
  log_section "E2E test prerequisites"
  local ok=0
  check_dotnet            || ok=1
  check_kubectl           || ok=1
  check_k3d               || ok=1
  check_container_runtime || ok=1
  check_java              || ok=1
  return $ok
}

check_performance_prereqs() {
  log_section "Performance test prerequisites"
  local ok=0
  check_dotnet || ok=1
  return $ok
}

check_performance_load_prereqs() {
  log_section "Performance load test prerequisites"
  local ok=0
  check_dotnet            || ok=1
  check_kubectl           || ok=1
  check_k3d               || ok=1
  check_container_runtime || ok=1
  check_k6                || ok=1
  return $ok
}

