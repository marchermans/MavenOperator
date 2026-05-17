/**
 * reposilite.js — k6 benchmark targeting Reposilite (comparison baseline).
 *
 * Run:
 *   k6 run reposilite.js \
 *     -e REPOSILITE_URL=http://reposilite-svc:8080 \
 *     -e DOWNLOAD_USER=reader -e DOWNLOAD_PASS=secret \
 *     --out json=summary-reposilite.json
 */

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import {
  REPOSILITE_URL, SMALL_JAR_PATH, LARGE_JAR_PATH, METADATA_PATH,
  AUTH_HEADER, SCENARIOS, THRESHOLDS, REPOSITORY,
} from "./setup.js";

export const options = {
  scenarios:  SCENARIOS,
  thresholds: THRESHOLDS,
  tags:       { target: "reposilite" },
};

const uploadCounter = new Counter("reposilite_uploads_total");
const downloadTrend = new Trend("reposilite_download_ms", true);

function headers() {
  return { Authorization: AUTH_HEADER };
}

// Reposilite serves artifacts at /releases/<path> (no /repository/ prefix)
function artifactUrl(path) {
  return `${REPOSILITE_URL}/${path}`;
}

export function download_small() {
  const url = artifactUrl(SMALL_JAR_PATH);
  const res = http.get(url, { headers: headers() });
  downloadTrend.add(res.timings.duration);
  check(res, { "download-small 200": (r) => r.status === 200 });
}

export function download_large() {
  const url = artifactUrl(LARGE_JAR_PATH);
  const res = http.get(url, { headers: headers() });
  downloadTrend.add(res.timings.duration);
  check(res, { "download-large 200": (r) => r.status === 200 });
}

export function upload() {
  const uid  = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const path = `${REPOSITORY}/com/example/upload-bench/${uid}/upload-bench-${uid}.jar`;
  const url  = artifactUrl(path);

  const data = new Uint8Array(51200).fill(0xAB);
  const res  = http.put(url, data.buffer, {
    headers: { ...headers(), "Content-Type": "application/octet-stream" },
  });
  uploadCounter.add(1);
  check(res, { "upload 2xx": (r) => r.status >= 200 && r.status < 300 });
}

export function metadata() {
  const url = artifactUrl(METADATA_PATH);
  const res = http.get(url, { headers: headers() });
  check(res, { "metadata 200": (r) => r.status === 200 });
}

export function mixed() {
  if (Math.random() < 0.8) {
    download_small();
  } else {
    upload();
  }
  sleep(0.1);
}

export default function () {
  download_small();
}

