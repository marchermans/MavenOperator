using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using Shouldly;
using System.Diagnostics;

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
}

