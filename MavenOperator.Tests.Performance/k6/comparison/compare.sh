#!/usr/bin/env bash
##
## compare.sh — Run both k6 benchmark suites and produce a side-by-side summary.
##
## Usage:
##   ./compare.sh \
##     --maven-operator-url http://maven-op-svc \
##     --reposilite-url     http://reposilite-svc:8080 \
##     --download-user reader --download-pass secret
##
## Outputs: summary.json (machine-readable gates result)
##

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

MAVEN_OPERATOR_URL="http://localhost:8081"
REPOSILITE_URL="http://localhost:8082"
DOWNLOAD_USER="downloader"
DOWNLOAD_PASS="password"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --maven-operator-url) MAVEN_OPERATOR_URL="$2"; shift 2;;
    --reposilite-url)     REPOSILITE_URL="$2";     shift 2;;
    --download-user)      DOWNLOAD_USER="$2";       shift 2;;
    --download-pass)      DOWNLOAD_PASS="$2";       shift 2;;
    *) echo "Unknown argument: $1"; exit 1;;
  esac
done

export MAVEN_OPERATOR_URL REPOSILITE_URL DOWNLOAD_USER DOWNLOAD_PASS

echo "=== Running MavenOperator benchmark ==="
k6 run "${SCRIPT_DIR}/maven-operator.js" \
  --out "json=${SCRIPT_DIR}/raw-maven-operator.json" \
  --summary-export "${SCRIPT_DIR}/summary-maven-operator.json" \
  -e MAVEN_OPERATOR_URL="${MAVEN_OPERATOR_URL}" \
  -e DOWNLOAD_USER="${DOWNLOAD_USER}" \
  -e DOWNLOAD_PASS="${DOWNLOAD_PASS}" \
  || true   # don't fail yet — compare after both runs

echo ""
echo "=== Running Reposilite benchmark ==="
k6 run "${SCRIPT_DIR}/reposilite.js" \
  --out "json=${SCRIPT_DIR}/raw-reposilite.json" \
  --summary-export "${SCRIPT_DIR}/summary-reposilite.json" \
  -e REPOSILITE_URL="${REPOSILITE_URL}" \
  -e DOWNLOAD_USER="${DOWNLOAD_USER}" \
  -e DOWNLOAD_PASS="${DOWNLOAD_PASS}" \
  || true

echo ""
echo "=== Comparing results ==="

# Parse p50 and p95 latencies and throughput from summary JSON files
python3 - <<'PYEOF'
import json, sys

def load(path):
    with open(path) as f:
        return json.load(f)

def get_metric(summary, metric, stat):
    try:
        return summary["metrics"][metric]["values"][stat]
    except (KeyError, TypeError):
        return None

mo = load("summary-maven-operator.json")
rs = load("summary-reposilite.json")

results = {}
gates_passed = True

for label, mo_key, rs_key, gate_factor in [
    ("p50_download_ms",  "http_req_duration", "http_req_duration", 1.10),
    ("p95_download_ms",  "http_req_duration", "http_req_duration", 1.10),
]:
    mo_p50 = get_metric(mo, mo_key, "p(50)")
    rs_p50 = get_metric(rs, rs_key, "p(50)")
    mo_p95 = get_metric(mo, mo_key, "p(95)")
    rs_p95 = get_metric(rs, rs_key, "p(95)")

    p50_pass = mo_p50 is None or rs_p50 is None or mo_p50 <= rs_p50 * gate_factor
    p95_pass = mo_p95 is None or rs_p95 is None or mo_p95 <= rs_p95 * gate_factor

    if not p50_pass:
        print(f"FAIL p50: MavenOperator={mo_p50:.1f}ms Reposilite={rs_p50:.1f}ms (gate={rs_p50*gate_factor:.1f}ms)")
        gates_passed = False
    else:
        print(f"PASS p50: MavenOperator={mo_p50 or 'N/A'}ms  Reposilite={rs_p50 or 'N/A'}ms")

    if not p95_pass:
        print(f"FAIL p95: MavenOperator={mo_p95:.1f}ms Reposilite={rs_p95:.1f}ms (gate={rs_p95*gate_factor:.1f}ms)")
        gates_passed = False
    else:
        print(f"PASS p95: MavenOperator={mo_p95 or 'N/A'}ms  Reposilite={rs_p95 or 'N/A'}ms")

mo_err = get_metric(mo, "http_req_failed", "rate")
if mo_err is not None and mo_err >= 0.001:
    print(f"FAIL error_rate: MavenOperator={mo_err*100:.3f}% (gate<0.1%)")
    gates_passed = False
else:
    print(f"PASS error_rate: MavenOperator={mo_err*100:.3f}%" if mo_err is not None else "PASS error_rate: N/A")

summary = {
    "gates_passed": gates_passed,
    "maven_operator": {
        "p50_ms": get_metric(mo, "http_req_duration", "p(50)"),
        "p95_ms": get_metric(mo, "http_req_duration", "p(95)"),
        "error_rate": get_metric(mo, "http_req_failed", "rate"),
    },
    "reposilite": {
        "p50_ms": get_metric(rs, "http_req_duration", "p(50)"),
        "p95_ms": get_metric(rs, "http_req_duration", "p(95)"),
        "error_rate": get_metric(rs, "http_req_failed", "rate"),
    },
}

with open("summary.json", "w") as f:
    json.dump(summary, f, indent=2)

print()
print(json.dumps(summary, indent=2))
sys.exit(0 if gates_passed else 1)
PYEOF

