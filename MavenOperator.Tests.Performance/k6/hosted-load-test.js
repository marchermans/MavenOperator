/**
 * k6 load test for a Hosted Maven repository.
 *
 * Usage:
 *   k6 run k6/hosted-load-test.js \
 *     -e REPO_URL=http://maven.example.com/repository/my-releases \
 *     -e AUTH_DOWNLOAD=reader:readpass \
 *     -e AUTH_UPLOAD=deployer:deploypass
 *
 * Thresholds (CI gates — test fails if breached):
 *   - p(95) download latency < 200 ms
 *   - p(95) upload latency   < 500 ms
 *   - error rate             < 1%
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';
import encoding from 'k6/encoding';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

const errorRate     = new Rate('error_rate');
const downloadTrend = new Trend('download_latency_ms', true);
const uploadTrend   = new Trend('upload_latency_ms',   true);

export const options = {
    stages: [
        { duration: '30s', target: 10 },  // ramp up
        { duration: '60s', target: 10 },  // sustained load
        { duration: '30s', target: 0  },  // ramp down
    ],
    thresholds: {
        error_rate:          ['rate<0.01'],
        download_latency_ms: ['p(95)<200'],
        upload_latency_ms:   ['p(95)<500'],
        http_req_failed:     ['rate<0.01'],
    },
};

const REPO_URL      = __ENV.REPO_URL      || 'http://localhost:8080/repository/test-hosted';
const AUTH_DOWNLOAD = __ENV.AUTH_DOWNLOAD || 'reader:readpass';
const AUTH_UPLOAD   = __ENV.AUTH_UPLOAD   || 'deployer:deploypass';

const downloadAuth = `Basic ${encoding.b64encode(AUTH_DOWNLOAD)}`;
const uploadAuth   = `Basic ${encoding.b64encode(AUTH_UPLOAD)}`;

/** setup() runs once before VUs start. Pre-uploads the artifact that download VUs will fetch. */
export function setup() {
    const path    = 'io/mavenoperator/loadtest/1.0.0/loadtest-1.0.0.jar';
    const url     = `${REPO_URL}/${path}`;
    const headers = { Authorization: uploadAuth, 'Content-Type': 'application/octet-stream' };
    const body    = 'hello-from-k6-load-test-setup';

    const res = http.put(url, body, { headers });
    if (res.status !== 201 && res.status !== 204) {
        console.error(`[setup] Pre-upload failed: HTTP ${res.status} — ${res.body}`);
    } else {
        console.log(`[setup] Pre-uploaded artifact at ${url}`);
    }
}

/** Default VU function: 90% downloads, 10% unique uploads. */
export default function () {
    if (Math.random() < 0.1) {
        // Upload a unique artifact version to avoid conflicts
        const version = `1.${Math.floor(Math.random() * 100_000)}.0`;
        const path    = `io/mavenoperator/loadtest/${version}/loadtest-${version}.jar`;
        const url     = `${REPO_URL}/${path}`;
        const headers = { Authorization: uploadAuth, 'Content-Type': 'application/octet-stream' };

        const start = Date.now();
        const res   = http.put(url, 'artifact-payload', { headers });
        uploadTrend.add(Date.now() - start);
        errorRate.add(res.status !== 201 && res.status !== 204);

        check(res, {
            'upload 201 or 204': r => r.status === 201 || r.status === 204,
        });
    } else {
        // Download the pre-uploaded stable artifact
        const path    = 'io/mavenoperator/loadtest/1.0.0/loadtest-1.0.0.jar';
        const url     = `${REPO_URL}/${path}`;
        const headers = { Authorization: downloadAuth };

        const start = Date.now();
        const res   = http.get(url, { headers });
        downloadTrend.add(Date.now() - start);
        errorRate.add(res.status !== 200);

        check(res, {
            'download 200':            r => r.status === 200,
            'download body not empty': r => (r.body || '').length > 0,
        });
    }

    sleep(0.1);
}

/** handleSummary() writes a JSON report for CI archiving. */
export function handleSummary(data) {
    return {
        'k6-summary.json': JSON.stringify(data, null, 2),
        stdout: textSummary(data, { indent: ' ', enableColors: true }),
    };
}

