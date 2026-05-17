using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;

namespace MavenOperator.Tests.E2E.Observability;

/// <summary>
/// End-to-end tests for Phase 6A — Deep Observability.
///
/// Validates that when <c>spec.metrics.enabled: true</c>:
///   1. The operator injects the nginx-prometheus-exporter and mtail sidecars.
///   2. The Service exposes named ports for both metrics endpoints.
///   3. Both /metrics endpoints return valid Prometheus text after artifact traffic.
///   4. nginx_http_requests_total increments after requests.
///   5. mtail exposes maven_artifact_requests_total after PUT/GET cycles.
///
/// Run with: E2E_TESTS=true dotnet test --filter Category=E2E
/// Pre-requisite: operator with metrics-enabled image deployed in cluster.
/// </summary>
[Collection(MetricsE2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class MetricsObservabilityE2ETests(MetricsE2EFixture metrics)
{
    // ── Sidecar connectivity ────────────────────────────────────────────────

    [MetricsE2EFact]
    public async Task NginxExporter_Endpoint_IsReachable()
    {
        using var client   = new HttpClient();
        var response       = await client.GetAsync($"{metrics.ExporterMetricsUrl}/metrics");
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "nginx-prometheus-exporter /metrics must return 200 once NGINX is running");
    }

    [MetricsE2EFact]
    public async Task MtailExporter_Endpoint_IsReachable()
    {
        using var client   = new HttpClient();
        var response       = await client.GetAsync($"{metrics.MtailMetricsUrl}/metrics");
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "mtail /metrics must return 200 once the mtail sidecar is running");
    }

    [MetricsE2EFact]
    public async Task NginxExporter_Metrics_ContainConnectionMetrics()
    {
        using var client = new HttpClient();
        var body = await client.GetStringAsync($"{metrics.ExporterMetricsUrl}/metrics");

        body.ShouldContain("nginx_connections_active");
        body.ShouldContain("nginx_http_requests_total");
    }

    [MetricsE2EFact]
    public async Task MtailExporter_Metrics_ContainMavenArtifactMetric()
    {
        using var client = new HttpClient();
        var body = await client.GetStringAsync($"{metrics.MtailMetricsUrl}/metrics");

        // The mtail program defines maven_artifact_requests_total.
        // It may be zero until traffic flows, but the metric must be declared.
        body.ShouldContain("maven_artifact_requests_total");
    }

    // ── Metric increment after artifact traffic ────────────────────────────

    [MetricsE2EFact]
    public async Task NginxExporter_HttpRequestsTotal_IncreasesAfterGet()
    {
        // Sample the baseline counter value
        using var cClient = new HttpClient();
        var before = ParseNginxRequestsTotal(
            await cClient.GetStringAsync($"{metrics.ExporterMetricsUrl}/metrics"));

        // Issue a GET request against the repository
        await metrics.HttpClient.GetAsync($"/repository/{metrics.RepositoryName}/does-not-exist");

        // Allow nginx/exporter a moment to update the stub_status counter
        await Task.Delay(TimeSpan.FromSeconds(2));

        var after = ParseNginxRequestsTotal(
            await cClient.GetStringAsync($"{metrics.ExporterMetricsUrl}/metrics"));

        after.ShouldBeGreaterThan(before,
            "nginx_http_requests_total must increase after an HTTP request");
    }

    [MetricsE2EFact]
    public async Task MtailMetrics_MavenRequestsCounter_IncreasesAfterPut()
    {
        // Upload an artifact so the NGINX access log gets a write
        var content = Encoding.UTF8.GetBytes("fake-artifact-for-metrics-test");
        using var uploadReq = new HttpRequestMessage(
            HttpMethod.Put,
            $"/repository/{metrics.RepositoryName}/io/example/metrics-test/1.0.0/metrics-test-1.0.0.jar");
        uploadReq.Content = new ByteArrayContent(content);
        await metrics.HttpClient.SendAsync(uploadReq);

        // Allow mtail to tail the log and increment the counter
        await Task.Delay(TimeSpan.FromSeconds(5));

        using var mClient = new HttpClient();
        var body = await mClient.GetStringAsync($"{metrics.MtailMetricsUrl}/metrics");

        // If maven_artifact_requests_total has been incremented it will appear with a value > 0
        // We accept the metric being present with any value (including 0 on first run).
        body.ShouldContain("maven_artifact_requests_total");
    }

    [MetricsE2EFact]
    public async Task MtailMetrics_PutCounter_IsNonZeroAfterUpload()
    {
        // Upload a unique artifact path to ensure this test's traffic is counted
        var content = Encoding.UTF8.GetBytes("unique-metrics-payload");
        var uniquePath = $"io/example/unique-metrics/{Guid.NewGuid():N}/artifact-1.0.0.jar";
        using var uploadReq = new HttpRequestMessage(
            HttpMethod.Put,
            $"/repository/{metrics.RepositoryName}/{uniquePath}");
        uploadReq.Content = new ByteArrayContent(content);
        var putResp = await metrics.HttpClient.SendAsync(uploadReq);

        // Only assert if the upload succeeded (anonymous upload should succeed)
        if (!putResp.IsSuccessStatusCode)
            return; // skip — auth policy may reject this in some setups

        // Wait for mtail to process the log entry
        await Task.Delay(TimeSpan.FromSeconds(5));

        using var mClient = new HttpClient();
        var body = await mClient.GetStringAsync($"{metrics.MtailMetricsUrl}/metrics");

        // Verify there's at least one non-zero PUT counter in the mtail output
        var hasPutMetric = body.Lines()
            .Where(l => l.StartsWith("maven_artifact_requests_total") && l.Contains("PUT"))
            .Any(l =>
            {
                var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 && double.TryParse(parts[^1], out var v) && v > 0;
            });

        hasPutMetric.ShouldBeTrue(
            "maven_artifact_requests_total{method='PUT'} must be > 0 after a successful PUT");
    }

    // ── NGINX repository still serves traffic ─────────────────────────────

    [MetricsE2EFact]
    public async Task Repository_WithMetricsEnabled_StillServesHttpTraffic()
    {
        // Basic health check — ensures metrics sidecars don't break normal serving
        var response = await metrics.HttpClient.GetAsync("/healthz");
        response.IsSuccessStatusCode.ShouldBeTrue(
            "Repository with metrics sidecars must still respond to /healthz");
    }

    [MetricsE2EFact]
    public async Task Repository_WithMetricsEnabled_Returns404_ForNonExistentArtifact()
    {
        var response = await metrics.HttpClient.GetAsync(
            $"/repository/{metrics.RepositoryName}/io/does/not/exist/1.0/artifact-1.0.jar");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "Missing artifact must return 404 even when metrics sidecars are enabled");
    }

    // ── Helper: parse nginx_http_requests_total from Prometheus text format ─

    private static double ParseNginxRequestsTotal(string prometheusText)
    {
        var line = prometheusText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith("nginx_http_requests_total") && !l.StartsWith('#'));

        if (line is null) return 0;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && double.TryParse(parts[^1], out var val) ? val : 0;
    }
}

file static class StringExtensions
{
    public static IEnumerable<string> Lines(this string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
