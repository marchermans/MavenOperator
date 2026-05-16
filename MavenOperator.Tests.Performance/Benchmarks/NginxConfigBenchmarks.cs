using BenchmarkDotNet.Attributes;
using MavenOperator.Entities.Spec;
using MavenOperator.Services;

namespace MavenOperator.Tests.Performance.Benchmarks;

/// <summary>
/// Benchmarks NGINX config rendering via Scriban.
/// The template is parsed once and cached; repeated renders must be sub-millisecond.
///
/// Run: dotnet run -c Release --project MavenOperator.Tests.Performance -- --benchmark
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 20)]
public class NginxConfigBenchmarks
{
    private readonly INginxConfigRenderer _renderer = new NginxConfigRenderer();

    [Benchmark(Baseline = true, Description = "Render Hosted config (Anonymous DL, Auth UL)")]
    public string RenderHostedAnonymousDownload()
        => _renderer.RenderHosted("my-releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated);

    [Benchmark(Description = "Render Hosted config (Auth DL, Auth UL)")]
    public string RenderHostedAuthenticatedDownload()
        => _renderer.RenderHosted("my-releases", AuthPolicy.Authenticated, AuthPolicy.Authenticated);
}

