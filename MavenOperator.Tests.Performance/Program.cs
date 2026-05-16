using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using MavenOperator.Tests.Performance.Benchmarks;

// When launched via `dotnet run -c Release -- --benchmark`, run BenchmarkDotNet.
// When launched via `dotnet test`, xUnit discovers PerformanceBaselineTests.
if (args.Contains("--benchmark"))
{
    // Optional: --artifacts <dir> redirects BenchmarkDotNet output.
    var artifactsIdx = Array.IndexOf(args, "--artifacts");
    var artifactsDir = artifactsIdx >= 0 && artifactsIdx + 1 < args.Length
        ? args[artifactsIdx + 1]
        : null;

    IConfig config = artifactsDir is not null
        ? DefaultConfig.Instance.WithArtifactsPath(artifactsDir)
        : DefaultConfig.Instance;

    Console.WriteLine("=== Running HtpasswdBenchmarks ===");
    BenchmarkRunner.Run<HtpasswdBenchmarks>(config);

    Console.WriteLine("=== Running NginxConfigBenchmarks ===");
    BenchmarkRunner.Run<NginxConfigBenchmarks>(config);
}
else
{
    Console.WriteLine("Performance test project loaded.");
    Console.WriteLine("Run benchmarks with:");
    Console.WriteLine("  dotnet run -c Release --project MavenOperator.Tests.Performance -- --benchmark");
    Console.WriteLine("  dotnet run -c Release --project MavenOperator.Tests.Performance -- --benchmark --artifacts .benchmarks");
    Console.WriteLine("Run xUnit smoke tests with:");
    Console.WriteLine("  dotnet test MavenOperator.Tests.Performance --filter Category=Performance");
}
