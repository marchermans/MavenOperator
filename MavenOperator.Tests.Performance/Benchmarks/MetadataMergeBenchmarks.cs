using BenchmarkDotNet.Attributes;
using MavenOperator.VirtualProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace MavenOperator.Tests.Performance.Benchmarks;

/// <summary>
/// Benchmarks for MetadataMergeService — validates that metadata merge latency
/// stays within acceptable bounds as member count grows.
///
/// Run: dotnet run -c Release --project MavenOperator.Tests.Performance -- --benchmark
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 15)]
public class MetadataMergeBenchmarks
{
    private MetadataMergeService _service = null!;
    private FakeHandler          _handler = null!;

    [Params(1, 3, 5, 10)]
    public int MemberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _handler = new FakeHandler(MemberCount);
        _service = new MetadataMergeService(
            new HttpClient(_handler),
            NullLogger<MetadataMergeService>.Instance);
    }

    [Benchmark(Description = "Merge maven-metadata.xml from N members")]
    public async Task<string?> MergeMetadataFromNMembers()
    {
        var baseUrls = Enumerable.Range(1, MemberCount)
            .Select(i => $"http://member{i}");

        return await _service.MergeMetadataAsync(
            baseUrls, "com/example/artifact/maven-metadata.xml", CancellationToken.None);
    }

    [Benchmark(Description = "Parse single maven-metadata.xml")]
    public MavenMetadata? ParseSingleMetadata()
        => MetadataMergeService.ParseMetadata(SampleXml(1));

    [Benchmark(Description = "Serialize merged metadata to XML string")]
    public string SerializeMerged()
    {
        var docs = Enumerable.Range(1, MemberCount)
            .Select(i => new MavenMetadata(
                "com.example", "artifact",
                Enumerable.Range(1, 5).Select(v => $"{i}.{v}.0").ToList(),
                $"{i}.5.0", $"{i}.5.0", $"2026051512{i:D4}"))
            .ToList();
        return MetadataMergeService.SerializeMetadata(MetadataMergeService.Merge(docs));
    }

    private static string SampleXml(int memberIdx)
    {
        var versions = string.Join("", Enumerable.Range(1, 5)
            .Select(v => $"<version>{memberIdx}.{v}.0</version>"));
        return $"""
            <metadata>
              <groupId>com.example</groupId>
              <artifactId>artifact</artifactId>
              <versioning>
                <versions>{versions}</versions>
                <latest>{memberIdx}.5.0</latest>
                <release>{memberIdx}.5.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;
    }

    private sealed class FakeHandler(int memberCount) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Find which member index this URL is targeting
            var url = request.RequestUri?.ToString() ?? "";
            for (var i = 1; i <= memberCount; i++)
            {
                if (url.Contains($"member{i}"))
                {
                    var xml = SampleXml(i);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

