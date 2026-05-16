using MavenOperator.VirtualProxy.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Net;
using System.Text;

namespace MavenOperator.Tests.Unit.VirtualProxy.Services;

/// <summary>
/// Unit tests for <see cref="VirtualProxyService"/>.
/// All HTTP calls are stubbed via a fake handler — no real cluster or network required.
/// </summary>
public sealed class VirtualProxyServiceTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose() => _cache.Dispose();

    // ── ForwardAsync — non-metadata paths ─────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_NonMetadata_ReturnsFirstSuccessfulMemberResponse()
    {
        var responses = new FakeResponses
        {
            ["http://member1/repository/member1/com/example/foo/1.0/foo-1.0.jar"] =
                OkBytes([0x50, 0x4B]),  // PK header
            ["http://member2/repository/member2/com/example/foo/1.0/foo-1.0.jar"] =
                OkBytes([0x00, 0x01]),
        };

        var svc = BuildService(["member1", "member2"], responses);

        var result = await svc.ForwardAsync(
            "com/example/foo/1.0/foo-1.0.jar", CancellationToken.None);

        result.ShouldNotBeNull();
        var bytes = await ReadAllAsync(result!.Content);
        bytes.ShouldBe([0x50, 0x4B]);
    }

    [Fact]
    public async Task ForwardAsync_NonMetadata_SkipsFirstMember_WhenItReturns404()
    {
        var responses = new FakeResponses
        {
            // member1 → 404, member2 → 200
            ["http://member2/repository/member2/com/example/foo/1.0/foo-1.0.jar"] =
                OkBytes([0xAB]),
        };

        var svc = BuildService(["member1", "member2"], responses);
        var result = await svc.ForwardAsync(
            "com/example/foo/1.0/foo-1.0.jar", CancellationToken.None);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ForwardAsync_NonMetadata_AllMembers404_ReturnsNull()
    {
        var svc = BuildService(["member1", "member2"], new FakeResponses());
        var result = await svc.ForwardAsync(
            "io/does/not/exist/1.0/x-1.0.jar", CancellationToken.None);

        result.ShouldBeNull();
    }

    // ── ForwardAsync — maven-metadata.xml ─────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_Metadata_MergesAllMemberResponses()
    {
        const string xmlA = """
            <metadata><groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>1.0</version></versions>
                <latest>1.0</latest><release>1.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;
        const string xmlB = """
            <metadata><groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>2.0</version></versions>
                <latest>2.0</latest><release>2.0</release>
                <lastUpdated>20260515130000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var responses = new FakeResponses
        {
            ["http://member1/repository/member1/g/a/maven-metadata.xml"] = OkXml(xmlA),
            ["http://member2/repository/member2/g/a/maven-metadata.xml"] = OkXml(xmlB),
        };

        var svc    = BuildService(["member1", "member2"], responses);
        var result = await svc.ForwardAsync("g/a/maven-metadata.xml", CancellationToken.None);

        result.ShouldNotBeNull();
        var xml = await ReadStringAsync(result!.Content);
        xml.ShouldContain("<version>1.0</version>");
        xml.ShouldContain("<version>2.0</version>");
        xml.ShouldContain("<latest>2.0</latest>");
    }

    [Fact]
    public async Task ForwardAsync_Metadata_AllMembers404_ReturnsNull()
    {
        var svc    = BuildService(["member1"], new FakeResponses());
        var result = await svc.ForwardAsync("g/a/maven-metadata.xml", CancellationToken.None);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ForwardAsync_Metadata_SecondCall_ServedFromCache()
    {
        const string xml = """
            <metadata><groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>1.0</version></versions>
                <latest>1.0</latest><release>1.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var handler = new CountingFakeHandler(
            url => url.Contains("maven-metadata") ? OkXml(xml) : NotFound());

        var svc = BuildServiceWithHandler(["member1"], handler);

        // First call — fetches from member
        await svc.ForwardAsync("g/a/maven-metadata.xml", CancellationToken.None);
        // Second call — must be served from cache (handler NOT called again)
        await svc.ForwardAsync("g/a/maven-metadata.xml", CancellationToken.None);

        // Only 1 HTTP call should have been made (the second is from cache)
        handler.CallCount.ShouldBe(1);
    }

    // ── ContentType propagation ────────────────────────────────────────────────

    [Fact]
    public async Task ForwardAsync_Metadata_SetsApplicationXmlContentType()
    {
        const string xml = "<metadata><groupId>g</groupId><artifactId>a</artifactId><versioning/></metadata>";
        var responses = new FakeResponses
        {
            ["http://member1/repository/member1/g/a/maven-metadata.xml"] = OkXml(xml),
        };

        var svc    = BuildService(["member1"], responses);
        var result = await svc.ForwardAsync("g/a/maven-metadata.xml", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.ContentType.ShouldContain("xml");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IVirtualProxyService BuildService(
        IEnumerable<string> memberNames,
        FakeResponses responses)
    {
        var handler = new FakeHttpHandler(responses);
        return BuildServiceWithHandler(memberNames, handler);
    }

    private IVirtualProxyService BuildServiceWithHandler(
        IEnumerable<string> memberNames,
        HttpMessageHandler handler)
    {
        var members = memberNames
            .Select(n => new VirtualMember(n, $"http://{n}/repository/{n}"))
            .ToList();

        var config = new VirtualRepoConfig
        {
            Name    = "test-virtual",
            Members = members,
            MetadataCacheTtlSeconds = 300,
        };

        var httpClient = new HttpClient(handler);
        var mergeService = new MetadataMergeService(
            new HttpClient(handler),
            NullLogger<MetadataMergeService>.Instance);

        return new VirtualProxyService(
            config,
            mergeService,
            httpClient,
            NullLogger<VirtualProxyService>.Instance,
            _cache,
            new VirtualProxyMetrics());
    }

    private static HttpResponseMessage OkBytes(byte[] data) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data),
        };

    private static HttpResponseMessage OkXml(string xml) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound);

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<string> ReadStringAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    // ── Fake HTTP handlers ────────────────────────────────────────────────────

    /// <summary>Returns pre-configured responses by URL; 404 for anything else.</summary>
    private sealed class FakeHttpHandler(FakeResponses responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(
                responses.TryGetValue(url, out var r) ? r : NotFound());
        }
    }

    /// <summary>Counts HTTP calls; delegates response to a factory func.</summary>
    private sealed class CountingFakeHandler(Func<string, HttpResponseMessage> factory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var url = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(factory(url));
        }
    }

    private sealed class FakeResponses : Dictionary<string, HttpResponseMessage>;
}

