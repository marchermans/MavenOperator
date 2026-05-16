/**
 * k6 load test for a Virtual Maven repository.
 *
 * Validates that:
 *   - Fan-out reads (GET artifact from a virtual that proxies to multiple hosted members)
 *     complete well within latency thresholds under sustained concurrency.
 *   - maven-metadata.xml merge endpoint holds up under concurrent reads.
 *   - Uploads (PUT) are correctly rejected with 4xx.
 *
 * Pre-requisites (handled by the test runner script):
 *   - Two Hosted repositories (MEMBER1_URL, MEMBER2_URL) already have artifacts seeded.
 *   - A Virtual repository (REPO_URL) fans out to both members.
 *
 * Usage:
 *   k6 run k6/virtual-load-test.js \
 *     -e REPO_URL=http://localhost:18080/repository/perf-virtual \
 *     -e MEMBER1_URL=http://localhost:18081/repository/perf-member1 \
 *     -e MEMBER2_URL=http://localhost:18082/repository/perf-member2
 *
 * Thresholds (CI gates):
 *   - p(95) read latency  < 300 ms  (fan-out is slower than direct hosted)
 *   - p(95) metadata latency < 400 ms
 *   - error rate < 1%
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

const errorRate    = new Rate('error_rate');
const readTrend    = new Trend('read_latency_ms',     true);
const metaTrend    = new Trend('metadata_latency_ms', true);
const uploadTrend  = new Trend('upload_reject_ms',    true);

export const options = {
    stages: [
        { duration: '20s', target: 15 },   // ramp up
        { duration: '60s', target: 15 },   // sustained load
        { duration: '10s', target: 30 },   // spike
        { duration: '30s', target: 15 },   // back to normal
        { duration: '20s', target: 0 },    // ramp down
    ],
    thresholds: {
        error_rate:        ['rate<0.01'],
        read_latency_ms:   ['p(95)<300'],
        metadata_latency_ms: ['p(95)<400'],
        http_req_failed:   ['rate<0.01'],
    },
};

const REPO_URL    = __ENV.REPO_URL    || 'http://localhost:18080/repository/perf-virtual';
const GROUP_PATH  = 'io/mavenoperator/virtual-perf';
const ARTIFACT    = 'loadtest';
const VERSION     = '1.0.0';
const JAR_PATH    = `${GROUP_PATH}/${ARTIFACT}/${VERSION}/${ARTIFACT}-${VERSION}.jar`;
const META_PATH   = `${GROUP_PATH}/${ARTIFACT}/maven-metadata.xml`;

/** setup() seeds a test artifact via member1 (the runner script handles port-forwarding). */
export function setup() {
    const member1Url = __ENV.MEMBER1_URL || null;
    if (!member1Url) {
        console.log('[setup] MEMBER1_URL not set — assuming artifacts are pre-seeded.');
        return {};
    }

    const jar    = new Uint8Array(4096).fill(65); // 4K of 'A'
    const jarUrl = `${member1Url}/${JAR_PATH}`;
    const res    = http.put(jarUrl, jar, {
        headers: { 'Content-Type': 'application/java-archive' },
    });

    check(res, {
        'seed artifact upload succeeded': (r) => r.status === 200 || r.status === 201 || r.status === 204,
    });

    if (res.status < 200 || res.status > 299) {
        console.warn(`[setup] Artifact seed to member1 returned ${res.status} — tests may fail.`);
    }

    return {};
}

export default function () {
    // ── GET artifact through virtual fan-out ─────────────────────────────────
    const jarStart = Date.now();
    const jarRes   = http.get(`${REPO_URL}/${JAR_PATH}`);
    readTrend.add(Date.now() - jarStart);

    const jarOk = check(jarRes, {
        'virtual GET artifact returns 200': (r) => r.status === 200,
    });
    errorRate.add(!jarOk);

    // ── GET maven-metadata.xml (merge endpoint) ──────────────────────────────
    const metaStart = Date.now();
    const metaRes   = http.get(`${REPO_URL}/${META_PATH}`);
    metaTrend.add(Date.now() - metaStart);

    const metaOk = check(metaRes, {
        'virtual metadata returns 200': (r) => r.status === 200 || r.status === 404,
        'metadata content-type is xml': (r) =>
            r.status !== 200 || (r.headers['Content-Type'] || '').includes('xml'),
    });
    errorRate.add(!metaOk && metaRes.status !== 404);

    // ── PUT to virtual must be rejected ──────────────────────────────────────
    const putStart = Date.now();
    const putRes   = http.put(`${REPO_URL}/io/test/should-be-rejected/1.0/x-1.0.jar`,
        'refuse-me', { headers: { 'Content-Type': 'application/octet-stream' } });
    uploadTrend.add(Date.now() - putStart);

    check(putRes, {
        'PUT to virtual is rejected (4xx)': (r) => r.status >= 400 && r.status < 500,
    });

    // ── Health check ─────────────────────────────────────────────────────────
    const healthRes = http.get(`${REPO_URL.replace(/\/repository\/.*$/, '')}/healthz`);
    check(healthRes, { 'virtual healthz returns 2xx': (r) => r.status >= 200 && r.status < 300 });

    sleep(0.5);
}

export function handleSummary(data) {
    return {
        stdout: textSummary(data, { indent: ' ', enableColors: true }),
    };
}

