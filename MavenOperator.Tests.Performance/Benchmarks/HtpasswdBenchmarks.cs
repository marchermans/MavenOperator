using BenchmarkDotNet.Attributes;
using MavenOperator.Services;

namespace MavenOperator.Tests.Performance.Benchmarks;

/// <summary>
/// Benchmarks the HtpasswdService — critical because bcrypt is intentionally slow
/// but must remain predictable when many users are configured.
///
/// Run: dotnet run -c Release --project MavenOperator.Tests.Performance -- --benchmark
/// Results are saved to /.benchmarks/
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class HtpasswdBenchmarks
{
    private readonly IHtpasswdService _service = new HtpasswdService();

    [Benchmark(Baseline = true, Description = "Hash 1 password (bcrypt WF=10)")]
    public string HashSinglePassword()
        => _service.HashPassword("bench-user", "bench-password");

    [Params(1, 5, 10, 25)]
    public int UserCount { get; set; }

    [Benchmark(Description = "Build combined htpasswd file")]
    public string BuildCombinedHtpasswd()
    {
        var creds = Enumerable.Range(1, UserCount)
            .Select(i => ($"user{i}", $"password{i}"));
        return _service.BuildHtpasswd(creds);
    }
}

