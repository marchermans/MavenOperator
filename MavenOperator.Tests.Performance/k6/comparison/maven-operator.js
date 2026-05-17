/**
 * maven-operator.js — k6 benchmark targeting MavenOperator Hosted repository.
 *
 * Run:
 *   k6 run maven-operator.js \
 *     -e MAVEN_OPERATOR_URL=http://my-operator-svc \
 *     -e DOWNLOAD_USER=reader -e DOWNLOAD_PASS=secret \
 *     --out json=summary-maven-operator.json
 */

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import {
  MAVEN_OPERATOR_URL, SMALL_JAR_PATH, LARGE_JAR_PATH, METADATA_PATH,
  AUTH_HEADER, SCENARIOS, THRESHOLDS, REPOSITORY, ENABLE_UPLOAD_SCENARIO, THINK_TIME_SECONDS,
} from "./setup.js";

export const options = {
  scenarios:  SCENARIOS,
  thresholds: THRESHOLDS,
  tags:       { target: "maven-operator" },
  discardResponseBodies: true,
};

const uploadCounter   = new Counter("maven_operator_uploads_total");
const downloadTrend   = new Trend("maven_operator_download_ms", true);

function headers() {
  return AUTH_HEADER ? { Authorization: AUTH_HEADER } : {};
}

// ── download-small scenario ──────────────────────────────────────────────────
export function download_small() {
  const url = `${MAVEN_OPERATOR_URL}/repository/${SMALL_JAR_PATH}`;
  const res = http.get(url, { headers: headers() });
  downloadTrend.add(res.timings.duration);
  check(res, { "download-small 200": (r) => r.status === 200 });
  sleep(THINK_TIME_SECONDS);
}

// ── download-large scenario ──────────────────────────────────────────────────
export function download_large() {
  const url = `${MAVEN_OPERATOR_URL}/repository/${LARGE_JAR_PATH}`;
  const res = http.get(url, { headers: headers() });
  downloadTrend.add(res.timings.duration);
  check(res, { "download-large 200": (r) => r.status === 200 });
  sleep(THINK_TIME_SECONDS);
}

// ── upload scenario ──────────────────────────────────────────────────────────
export function upload() {
  const uid  = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const path = `${REPOSITORY}/com/example/upload-bench/${uid}/upload-bench-${uid}.jar`;
  const url  = `${MAVEN_OPERATOR_URL}/repository/${path}`;

  const data = new Uint8Array(51200).fill(0xAB);   // 50 KB of 0xAB
  const res  = http.put(url, data.buffer, {
    headers: { ...headers(), "Content-Type": "application/octet-stream" },
  });
  uploadCounter.add(1);
  check(res, { "upload 201/204": (r) => r.status === 201 || r.status === 204 || r.status === 200 });
  sleep(THINK_TIME_SECONDS);
}

// ── metadata scenario ────────────────────────────────────────────────────────
export function metadata() {
  const url = `${MAVEN_OPERATOR_URL}/repository/${METADATA_PATH}`;
  const res = http.get(url, { headers: headers() });
  check(res, { "metadata 200": (r) => r.status === 200 });
  sleep(THINK_TIME_SECONDS);
}

// ── mixed scenario (80% download, 20% upload) ────────────────────────────────
export function mixed() {
  if (!ENABLE_UPLOAD_SCENARIO || Math.random() < 0.8) {
    download_small();
  } else {
    upload();
  }
  sleep(0.1);
}

export default function () {
  download_small();
}

