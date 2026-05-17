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

// Credentials encoded as Basic auth header
export const AUTH_HEADER = `Basic ${btoa(`${DOWNLOAD_USER}:${DOWNLOAD_PASS}`)}`;

// Seed artifact paths — must exist in both repos before the benchmark runs
export const SMALL_JAR_PATH  = `${REPOSITORY}/com/example/benchmark-small/1.0/benchmark-small-1.0.jar`;
export const LARGE_JAR_PATH  = `${REPOSITORY}/com/example/benchmark-large/1.0/benchmark-large-1.0.jar`;
export const METADATA_PATH   = `${REPOSITORY}/com/example/benchmark-small/maven-metadata.xml`;

// k6 scenario definitions — imported and re-used by both target scripts
export const SCENARIOS = {
  download_small: {
    executor:          "constant-vus",
    vus:               100,
    duration:          "2m",
    gracefulStop:      "10s",
    tags:              { scenario: "download-small" },
  },
  download_large: {
    executor:          "constant-vus",
    vus:               20,
    duration:          "2m",
    gracefulStop:      "10s",
    tags:              { scenario: "download-large" },
  },
  upload: {
    executor:          "constant-vus",
    vus:               10,
    duration:          "2m",
    gracefulStop:      "10s",
    tags:              { scenario: "upload" },
  },
  metadata: {
    executor:          "constant-vus",
    vus:               50,
    duration:          "1m",
    gracefulStop:      "10s",
    tags:              { scenario: "metadata" },
  },
  mixed: {
    executor:          "constant-vus",
    vus:               40,
    duration:          "5m",
    gracefulStop:      "30s",
    tags:              { scenario: "mixed" },
  },
};

// Success thresholds — gate values a failing benchmark will breach
export const THRESHOLDS = {
  http_req_duration: ["p(50)<500", "p(95)<2000"],
  http_req_failed:   ["rate<0.001"],
};

