/**
 * concurrent-download-test.js — High-concurrency download stress test
 *
 * Tests two distinct download patterns with 100+ simultaneous clients:
 *
 *   Scenario A — "Hot artifact" (thundering herd)
 *     All VUs request the exact same file path concurrently.
 *     Validates that NGINX handles high fan-in on a single file without
 *     file-descriptor exhaustion, lock contention, or error spikes.
 *     Threshold: p(95) < 300 ms at 120 VUs sustained.
 *
 *   Scenario B — "Catalogue sweep" (path diversity)
 *     Each VU picks a random artifact from a pre-seeded catalogue of
 *     CATALOGUE_SIZE distinct files.  Simulates a real Maven resolver
 *     pulling transitive dependencies — different paths, same concurrency.
 *     Threshold: p(95) < 500 ms at 100 VUs sustained.
 *
 * Usage:
 *   k6 run k6/concurrent-download-test.js \
 *     -e REPO_URL=http://localhost:8080/repository/perf-hosted \
 *     -e AUTH_DOWNLOAD=anon:anon \
 *     -e AUTH_UPLOAD=anon:anon \
 *     [-e CATALOGUE_SIZE=50] \
 *     [-e ARTIFACT_SIZE_BYTES=102400]
 *
 * Environment variables:
 *   REPO_URL           Base repository URL (no trailing slash)
 *   AUTH_DOWNLOAD      "user:pass" for download (use "anon:anon" for anonymous repos)
 *   AUTH_UPLOAD        "user:pass" for pre-seeding uploads in setup()
 *   CATALOGUE_SIZE     Number of distinct artefacts to pre-seed (default: 50)
 *   ARTIFACT_SIZE_BYTES  Payload size in bytes for each seeded artefact (default: 102400 = 100 KB)
 *   HOT_VUS            Peak VUs for the hot-artifact scenario (default: 120)
 *   CATALOGUE_VUS      Peak VUs for the catalogue-sweep scenario (default: 100)
 */

import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import encoding from 'k6/encoding';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

// ── Custom metrics ────────────────────────────────────────────────────────────

const hotErrorRate       = new Rate('hot_error_rate');
const hotLatency         = new Trend('hot_download_latency_ms',       true);
const hotThroughputBytes = new Counter('hot_downloaded_bytes');

const catErrorRate       = new Rate('catalogue_error_rate');
const catLatency         = new Trend('catalogue_download_latency_ms', true);
const catThroughputBytes = new Counter('catalogue_downloaded_bytes');

// ── Config ────────────────────────────────────────────────────────────────────

const REPO_URL         = __ENV.REPO_URL          || 'http://localhost:8080/repository/perf-hosted';
const AUTH_DOWNLOAD    = __ENV.AUTH_DOWNLOAD      || 'anon:anon';
const AUTH_UPLOAD      = __ENV.AUTH_UPLOAD        || 'anon:anon';
const CATALOGUE_SIZE   = parseInt(__ENV.CATALOGUE_SIZE     || '50',     10);
const ARTIFACT_BYTES   = parseInt(__ENV.ARTIFACT_SIZE_BYTES || '102400', 10); // 100 KB default
const HOT_VUS          = parseInt(__ENV.HOT_VUS            || '120',    10);
const CATALOGUE_VUS    = parseInt(__ENV.CATALOGUE_VUS      || '100',    10);

const downloadHeaders  = { Authorization: `Basic ${encoding.b64encode(AUTH_DOWNLOAD)}` };
const uploadHeaders    = {
    Authorization:  `Basic ${encoding.b64encode(AUTH_UPLOAD)}`,
    'Content-Type': 'application/octet-stream',
};

// Single "hot" artifact path — all VUs in scenario A race for this.
const HOT_PATH    = 'io/mavenoperator/perf/hot/1.0.0/hot-artifact-1.0.0.jar';
const HOT_URL     = `${REPO_URL}/${HOT_PATH}`;

// Catalogue paths — pre-seeded, each VU picks one at random in scenario B.
function cataloguePath(i) {
    const group_  = String(i).padStart(3, '0');
    return `io/mavenoperator/perf/catalogue/artifact-${group_}/1.0.0/artifact-${group_}-1.0.0.jar`;
}

// ── k6 scenario config ────────────────────────────────────────────────────────

export const options = {
    scenarios: {
        // Scenario A: thundering herd on the same file.
        hot_artifact: {
            executor:        'ramping-vus',
            startVUs:        0,
            stages: [
                { duration: '20s', target: HOT_VUS },  // ramp up to full concurrency
                { duration: '60s', target: HOT_VUS },  // sustained: 120 VUs on a single file
                { duration: '10s', target: 0 },         // ramp down
            ],
            gracefulRampDown: '5s',
            exec: 'hotArtifactScenario',
        },

        // Scenario B: sweeping through a catalogue of distinct artifacts.
        // Starts after scenario A completes to isolate measurements.
        catalogue_sweep: {
            executor:        'ramping-vus',
            startVUs:        0,
            startTime:       '100s',                    // begin after hot scenario ends
            stages: [
                { duration: '20s', target: CATALOGUE_VUS },
                { duration: '60s', target: CATALOGUE_VUS },
                { duration: '10s', target: 0 },
            ],
            gracefulRampDown: '5s',
            exec: 'catalogueSweepScenario',
        },
    },

    thresholds: {
        // Hot-artifact scenario: very tight latency — NGINX must serve from filesystem cache.
        'hot_error_rate':            ['rate<0.01'],
        'hot_download_latency_ms':   ['p(95)<300', 'p(99)<500'],

        // Catalogue scenario: wider spread of paths, slightly relaxed threshold.
        'catalogue_error_rate':      ['rate<0.01'],
        'catalogue_download_latency_ms': ['p(95)<500', 'p(99)<1000'],

        // Overall HTTP failure rate from k6's built-in metric.
        'http_req_failed':           ['rate<0.01'],
    },
};

// ── setup(): pre-seed the repository before VUs start ────────────────────────

export function setup() {
    console.log(`[setup] Pre-seeding hot artifact (${ARTIFACT_BYTES} bytes)…`);
    const hotPayload = generatePayload(ARTIFACT_BYTES);
    const hotRes     = http.put(HOT_URL, hotPayload, { headers: uploadHeaders });
    if (hotRes.status !== 201 && hotRes.status !== 204) {
        console.error(`[setup] Hot artifact upload FAILED: HTTP ${hotRes.status} ${hotRes.body}`);
    } else {
        console.log(`[setup] Hot artifact ready at ${HOT_URL}`);
    }

    console.log(`[setup] Pre-seeding catalogue of ${CATALOGUE_SIZE} artifacts…`);
    let failures = 0;
    for (let i = 0; i < CATALOGUE_SIZE; i++) {
        const path    = cataloguePath(i);
        const url     = `${REPO_URL}/${path}`;
        // Each catalogue artifact gets a slightly different payload so they aren't identical.
        const payload = generatePayload(ARTIFACT_BYTES, i);
        const res     = http.put(url, payload, { headers: uploadHeaders });
        if (res.status !== 201 && res.status !== 204) {
            failures++;
            console.warn(`[setup] Catalogue[${i}] upload failed: HTTP ${res.status}`);
        }
    }
    if (failures > 0) {
        console.error(`[setup] ${failures}/${CATALOGUE_SIZE} catalogue artifacts failed to upload.`);
    } else {
        console.log(`[setup] All ${CATALOGUE_SIZE} catalogue artifacts ready.`);
    }

    return { catalogueSize: CATALOGUE_SIZE, artifactBytes: ARTIFACT_BYTES };
}

// ── Scenario A: hot artifact (thundering herd) ────────────────────────────────

export function hotArtifactScenario() {
    group('hot_artifact', () => {
        const start = Date.now();
        const res   = http.get(HOT_URL, { headers: downloadHeaders });
        const ms    = Date.now() - start;

        hotLatency.add(ms);
        hotErrorRate.add(res.status !== 200);
        if (res.status === 200 && res.body) {
            hotThroughputBytes.add(res.body.length);
        }

        check(res, {
            'hot: status 200':          r => r.status === 200,
            'hot: body not empty':      r => (r.body || '').length > 0,
            'hot: content-length > 0':  r => parseInt(r.headers['Content-Length'] || '0', 10) > 0,
        });
    });

    // Minimal think-time — keep maximum pressure on the server.
    sleep(0.05);
}

// ── Scenario B: catalogue sweep (path diversity) ──────────────────────────────

export function catalogueSweepScenario(data) {
    const size = data?.catalogueSize ?? CATALOGUE_SIZE;

    group('catalogue_sweep', () => {
        // Pick a random artifact from the catalogue.
        const idx  = Math.floor(Math.random() * size);
        const path = cataloguePath(idx);
        const url  = `${REPO_URL}/${path}`;

        const start = Date.now();
        const res   = http.get(url, { headers: downloadHeaders });
        const ms    = Date.now() - start;

        catLatency.add(ms);
        catErrorRate.add(res.status !== 200);
        if (res.status === 200 && res.body) {
            catThroughputBytes.add(res.body.length);
        }

        check(res, {
            'catalogue: status 200':     r => r.status === 200,
            'catalogue: body not empty': r => (r.body || '').length > 0,
        });
    });

    // Simulate realistic Maven resolver pacing.
    sleep(0.1);
}

// ── handleSummary(): rich CI output ──────────────────────────────────────────

export function handleSummary(data) {
    // Emit a human-readable breakdown of latency percentiles per scenario.
    const fmt = (metric, label) => {
        const m = data.metrics[metric];
        if (!m) return `  ${label}: (no data)\n`;
        const p50  = (m.values['p(50)']  || 0).toFixed(1);
        const p95  = (m.values['p(95)']  || 0).toFixed(1);
        const p99  = (m.values['p(99)']  || 0).toFixed(1);
        const max  = (m.values['max']    || 0).toFixed(1);
        const avg  = (m.values['avg']    || 0).toFixed(1);
        return `  ${label}:  avg=${avg}ms  p50=${p50}ms  p95=${p95}ms  p99=${p99}ms  max=${max}ms\n`;
    };

    const fmtRate = (metric, label) => {
        const m = data.metrics[metric];
        if (!m) return `  ${label}: (no data)\n`;
        return `  ${label}: ${(m.values.rate * 100).toFixed(2)}%\n`;
    };

    const fmtBytes = (metric, label) => {
        const m = data.metrics[metric];
        if (!m) return `  ${label}: (no data)\n`;
        const mb = ((m.values.count || 0) / 1_048_576).toFixed(1);
        return `  ${label}: ${mb} MiB total\n`;
    };

    const report = [
        '\n╔══════════════════════════════════════════════════════════╗',
        '║      MavenOperator — Concurrent Download Test Results     ║',
        '╚══════════════════════════════════════════════════════════╝\n',
        '▶ Scenario A — Hot Artifact (thundering herd):\n',
        fmt('hot_download_latency_ms',  'Latency '),
        fmtRate('hot_error_rate',       'Errors  '),
        fmtBytes('hot_downloaded_bytes','Throughput'),
        '\n▶ Scenario B — Catalogue Sweep (path diversity):\n',
        fmt('catalogue_download_latency_ms',  'Latency '),
        fmtRate('catalogue_error_rate',       'Errors  '),
        fmtBytes('catalogue_downloaded_bytes','Throughput'),
        '\n',
    ].join('');

    return {
        'k6-concurrent-download-summary.json': JSON.stringify(data, null, 2),
        stdout: textSummary(data, { indent: ' ', enableColors: true }) + report,
    };
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/**
 * Generates a deterministic payload of exactly `bytes` size.
 * The `seed` parameter varies the pattern so catalogue artifacts differ.
 */
function generatePayload(bytes, seed = 0) {
    // Build a repeating ASCII pattern seeded by index.
    const pattern = `artifact-data-seed-${seed}-`.repeat(64);
    let result = '';
    while (result.length < bytes) {
        result += pattern;
    }
    return result.slice(0, bytes);
}

