import encoding from "k6/encoding";

/**
 * setup.js — Shared configuration for MavenOperator vs Reposilite comparison benchmarks.
 * Import this file in both maven-operator.js and reposilite.js.
 */

// URLs are injected via k6 --env flags or environment variables
export const MAVEN_OPERATOR_URL = __ENV.MAVEN_OPERATOR_URL || "http://localhost:8081";
export const REPOSILITE_URL     = __ENV.REPOSILITE_URL     || "http://localhost:8082";

export const REPOSITORY     = __ENV.REPOSITORY || "releases";
export const DOWNLOAD_USER  = __ENV.DOWNLOAD_USER  || "downloader";
export const DOWNLOAD_PASS  = __ENV.DOWNLOAD_PASS  || "password";
export const ENABLE_UPLOAD_SCENARIO = (__ENV.ENABLE_UPLOAD_SCENARIO || "false").toLowerCase() === "true";
export const PARALLEL_SCENARIOS = (__ENV.PARALLEL_SCENARIOS || "false").toLowerCase() === "true";
export const THINK_TIME_SECONDS = Number(__ENV.THINK_TIME_SECONDS || "0.05");

const DOWNLOAD_SMALL_VUS = Number(__ENV.DOWNLOAD_SMALL_VUS || "100");
const DOWNLOAD_LARGE_VUS = Number(__ENV.DOWNLOAD_LARGE_VUS || "20");
const METADATA_VUS = Number(__ENV.METADATA_VUS || "50");
const MIXED_VUS = Number(__ENV.MIXED_VUS || "40");

const DOWNLOAD_SMALL_DURATION = __ENV.DOWNLOAD_SMALL_DURATION || "2m";
const DOWNLOAD_LARGE_DURATION = __ENV.DOWNLOAD_LARGE_DURATION || "2m";
const METADATA_DURATION = __ENV.METADATA_DURATION || "1m";
const MIXED_DURATION = __ENV.MIXED_DURATION || "5m";

// Credentials encoded as Basic auth header
const useBasicAuth = !(DOWNLOAD_USER === "anon" && DOWNLOAD_PASS === "anon");
export const AUTH_HEADER = useBasicAuth
  ? `Basic ${encoding.b64encode(`${DOWNLOAD_USER}:${DOWNLOAD_PASS}`)}`
  : "";

// Seed artifact paths — must exist in both repos before the benchmark runs
export const SMALL_JAR_PATH  = `${REPOSITORY}/com/example/benchmark-small/1.0/benchmark-small-1.0.jar`;
export const LARGE_JAR_PATH  = `${REPOSITORY}/com/example/benchmark-large/1.0/benchmark-large-1.0.jar`;
export const METADATA_PATH   = `${REPOSITORY}/com/example/benchmark-small/maven-metadata.xml`;

// k6 scenario definitions — imported and re-used by both target scripts
const baseScenarios = {
  download_small: {
    executor:          "constant-vus",
    vus:               DOWNLOAD_SMALL_VUS,
    duration:          DOWNLOAD_SMALL_DURATION,
    gracefulStop:      "10s",
    tags:              { scenario: "download-small" },
  },
  download_large: {
    executor:          "constant-vus",
    vus:               DOWNLOAD_LARGE_VUS,
    duration:          DOWNLOAD_LARGE_DURATION,
    gracefulStop:      "10s",
    tags:              { scenario: "download-large" },
  },
  metadata: {
    executor:          "constant-vus",
    vus:               METADATA_VUS,
    duration:          METADATA_DURATION,
    gracefulStop:      "10s",
    tags:              { scenario: "metadata" },
  },
  mixed: {
    executor:          "constant-vus",
    vus:               MIXED_VUS,
    duration:          MIXED_DURATION,
    gracefulStop:      "30s",
    tags:              { scenario: "mixed" },
  },
};

if (!PARALLEL_SCENARIOS) {
  // Default to sequential scenarios for reproducible comparisons on local k3d.
  baseScenarios.download_large.startTime = "130s";
  baseScenarios.metadata.startTime = "260s";
  baseScenarios.mixed.startTime = "340s";
}

if (ENABLE_UPLOAD_SCENARIO) {
  baseScenarios.upload = {
    executor:     "constant-vus",
    vus:          10,
    duration:     "2m",
    gracefulStop: "10s",
    tags:         { scenario: "upload" },
  };
}

export const SCENARIOS = baseScenarios;

// Success thresholds — gate values a failing benchmark will breach
export const THRESHOLDS = {
  http_req_duration: ["p(50)<500", "p(95)<2000"],
  http_req_failed:   ["rate<0.001"],
};

