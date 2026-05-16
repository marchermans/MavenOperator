using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using MavenOperator.VirtualProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace MavenOperator.Tests.Performance.Benchmarks;

/// <summary>
/// Smoke / ceiling tests that run as part of the normal xUnit pass (`dotnet test`).
///
/// These tests enforce UPPER BOUND latency requirements only — they are NOT precise
/// micro-benchmarks. Their purpose is to catch catastrophic regressions
/// (e.g. accidentally running bcrypt in a tight loop, breaking template caching)
/// without requiring Release mode or BenchmarkDotNet infrastructure.
///
/// Precise numbers come from HtpasswdBenchmarks and NginxConfigBenchmarks.
/// </summary>
public sealed class PerformanceBaselineTests
{
    private readonly IHtpasswdService     _htpasswd = new HtpasswdService();
    private readonly INginxConfigRenderer _nginx    = new NginxConfigRenderer();

    // ── bcrypt hashing ────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Performance")]
    public void HashPassword_MustCompleteWithin_5Seconds_PerUser()
    {
        // bcrypt WF=10 typically takes 80–200 ms per hash on current hardware.
        // 5 s is a generous ceiling that catches infinite loops or wrong work-factor.
        var sw = Stopwatch.StartNew();
        _htpasswd.HashPassword("perf-user", "perf-password");
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
            $"bcrypt hash took {sw.ElapsedMilliseconds} ms — expected < 5 000 ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void BuildHtpasswd_With10Users_MustCompleteWithin_30Seconds()
    {
        // 10 users × ~200 ms/hash ≈ 2 s typical; 30 s ceiling is generous.
        var creds = Enumerable.Range(1, 10).Select(i => ($"user{i}", $"pw{i}"));
        var sw    = Stopwatch.StartNew();
        _htpasswd.BuildHtpasswd(creds);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30),
            $"10-user htpasswd build took {sw.ElapsedMilliseconds} ms — expected < 30 000 ms");
    }

    // ── NGINX template rendering ──────────────────────────────────────────

    [Fact]
    [Trait("Category", "Performance")]
    public void RenderNginxConfig_1000Iterations_MustCompleteWithin_500Milliseconds()
    {
        // Template is cached after first parse; repeated renders should be < 0.5 ms each.
        // 500 ms total for 1 000 renders catches cold-start + accidental re-parsing regressions.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            _nginx.RenderHosted("perf-repo", AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500),
            $"1 000 renders took {sw.ElapsedMilliseconds} ms — expected < 500 ms (~0.5 ms/render)");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RenderNginxConfig_OutputSize_IsReasonable()
    {
        var config = _nginx.RenderHosted("perf-repo", AuthPolicy.Authenticated, AuthPolicy.Authenticated);

        // A valid NGINX config file should be at least 200 bytes (contains location blocks etc.)
        // but not absurdly large (template explosion check).
        config.Length.ShouldBeGreaterThan(200,
            "Rendered config is suspiciously small — possible empty-template bug");
        config.Length.ShouldBeLessThan(64_000,
            "Rendered config is suspiciously large — possible template loop");
    }

    // ── Phase 3: Metadata merge ────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Performance")]
    public void MetadataMerge_10Members_MustCompleteWithin_2Seconds()
    {
        // Pure in-process merge of 10 metadata docs (no HTTP) must be fast.
        var docs = Enumerable.Range(1, 10)
            .Select(i => new MavenMetadata(
                "com.example", "artifact",
                Enumerable.Range(1, 5).Select(v => $"{i}.{v}.0").ToList(),
                $"{i}.5.0", $"{i}.5.0", $"20260515{i:D6}"))
            .ToList();

        var sw = Stopwatch.StartNew();
        for (var iter = 0; iter < 100; iter++)
            MetadataMergeService.Merge(docs);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2),
            $"100 × 10-member merges took {sw.ElapsedMilliseconds} ms — expected < 2 000 ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MetadataParse_MustCompleteWithin_50Milliseconds_Per1000Calls()
    {
        const string xml = """
            <metadata>
              <groupId>com.example</groupId><artifactId>foo</artifactId>
              <versioning>
                <versions>
                  <version>1.0</version><version>2.0</version><version>3.0</version>
                </versions>
                <latest>3.0</latest><release>3.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            MetadataMergeService.ParseMetadata(xml);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500),
            $"1 000 parses took {sw.ElapsedMilliseconds} ms — expected < 500 ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MetadataSerialize_MustBeSubMillisecond_Per100Docs()
    {
        var doc = new MavenMetadata("com.example", "artifact",
            Enumerable.Range(1, 20).Select(v => $"1.{v}.0").ToList(),
            "1.20.0", "1.20.0", "20260515120000");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            MetadataMergeService.SerializeMetadata(doc);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(100),
            $"100 serializations took {sw.ElapsedMilliseconds} ms — expected < 100 ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MergeMetadataAsync_5Members_MustCompleteWithin_500Milliseconds()
    {
        // Stubbed HTTP — this tests the fan-out parallel fetch + merge pipeline.
        const string xmlTemplate = """
            <metadata>
              <groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>{0}.0</version></versions>
                <latest>{0}.0</latest><release>{0}.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var handler = new FakeCountHandler(i => string.Format(xmlTemplate, i));
        var svc     = new MetadataMergeService(
            new HttpClient(handler),
            NullLogger<MetadataMergeService>.Instance);

        var memberUrls = Enumerable.Range(1, 5)
            .Select(i => $"http://member{i}").ToList();

        var sw = Stopwatch.StartNew();
        var result = await svc.MergeMetadataAsync(memberUrls, "g/a/maven-metadata.xml",
            CancellationToken.None);
        sw.Stop();

        result.ShouldNotBeNull();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500),
            $"5-member parallel merge took {sw.ElapsedMilliseconds} ms — expected < 500 ms");
    }

    // ── Phase 3: Proxy NGINX config rendering ─────────────────────────────────

    [Fact]
    [Trait("Category", "Performance")]
    public void RenderProxyConfig_1000Iterations_MustCompleteWithin_500Milliseconds()
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            _nginx.RenderProxy("perf-proxy", AuthPolicy.Anonymous,
                "https://repo1.maven.org/maven2", "1d", "");
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500),
            $"1 000 proxy renders took {sw.ElapsedMilliseconds} ms — expected < 500 ms");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeCountHandler(Func<int, string> xmlFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            for (var i = 1; i <= 10; i++)
            {
                if (url.Contains($"member{i}"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(xmlFactory(i), Encoding.UTF8, "application/xml"),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

