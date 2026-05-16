using MavenOperator.VirtualProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Net;
using System.Text;

namespace MavenOperator.Tests.Unit.VirtualProxy.Services;

/// <summary>
/// Unit tests for MetadataMergeService.
/// These tests cover the pure merge logic (static methods) as well as the
/// HTTP-based fetch + merge path using a stubbed HttpMessageHandler.
/// </summary>
public sealed class MetadataMergeServiceTests
{
    // ── ParseMetadata ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseMetadata_ValidXml_ReturnsMetadata()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <groupId>com.example</groupId>
              <artifactId>foo</artifactId>
              <versioning>
                <versions>
                  <version>1.0</version>
                  <version>1.1</version>
                </versions>
                <latest>1.1</latest>
                <release>1.1</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var result = MetadataMergeService.ParseMetadata(xml);

        result.ShouldNotBeNull();
        result.GroupId.ShouldBe("com.example");
        result.ArtifactId.ShouldBe("foo");
        result.Versions.ShouldBe(["1.0", "1.1"]);
        result.Latest.ShouldBe("1.1");
        result.Release.ShouldBe("1.1");
        result.LastUpdated.ShouldBe("20260515120000");
    }

    [Fact]
    public void ParseMetadata_InvalidXml_ReturnsNull()
    {
        var result = MetadataMergeService.ParseMetadata("not xml at all");
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseMetadata_EmptyVersions_ReturnsMetadataWithNoVersions()
    {
        const string xml = """
            <metadata>
              <groupId>g</groupId>
              <artifactId>a</artifactId>
              <versioning/>
            </metadata>
            """;

        var result = MetadataMergeService.ParseMetadata(xml);
        result.ShouldNotBeNull();
        result.Versions.ShouldBeEmpty();
    }

    // ── Merge ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_TwoMetadataDocs_UnionVersionsSorted()
    {
        var a = new MavenMetadata("com.example", "foo",
            ["1.0", "1.1"], "1.1", "1.1", "20260515120000");
        var b = new MavenMetadata("com.example", "foo",
            ["2.0"], "2.0", "2.0", "20260515130000");

        var merged = MetadataMergeService.Merge([a, b]);

        merged.Versions.ShouldBe(["1.0", "1.1", "2.0"]);
        merged.Latest.ShouldBe("2.0");
        merged.Release.ShouldBe("2.0");
        merged.LastUpdated.ShouldBe("20260515130000");
    }

    [Fact]
    public void Merge_DuplicateVersionsAcrossMembers_Deduplicated()
    {
        var a = new MavenMetadata("g", "a", ["1.0", "1.1"], "1.1", "1.1", "20260515120000");
        var b = new MavenMetadata("g", "a", ["1.1", "2.0"], "2.0", "2.0", "20260515130000");

        var merged = MetadataMergeService.Merge([a, b]);

        merged.Versions.ShouldBe(["1.0", "1.1", "2.0"]);
    }

    [Fact]
    public void Merge_SnapshotVersion_ExcludedFromRelease()
    {
        var a = new MavenMetadata("g", "a",
            ["1.0", "2.0-SNAPSHOT"], "2.0-SNAPSHOT", "1.0", "20260515120000");
        var b = new MavenMetadata("g", "a",
            ["1.5"], "1.5", "1.5", "20260514000000");

        var merged = MetadataMergeService.Merge([a, b]);

        merged.Release.ShouldBe("1.5");
        merged.Latest.ShouldBe("2.0-SNAPSHOT"); // snapshots are valid "latest"
    }

    [Fact]
    public void Merge_EmptyList_Throws()
    {
        Should.Throw<ArgumentException>(() => MetadataMergeService.Merge([]));
    }

    [Fact]
    public void Merge_SingleDoc_ReturnsSameValues()
    {
        var a = new MavenMetadata("g", "a", ["1.0"], "1.0", "1.0", "20260515120000");
        var merged = MetadataMergeService.Merge([a]);

        merged.Versions.ShouldBe(["1.0"]);
        merged.Latest.ShouldBe("1.0");
        merged.Release.ShouldBe("1.0");
        merged.LastUpdated.ShouldBe("20260515120000");
    }

    [Fact]
    public void Merge_LastUpdatedPicked_MaxAcrossMembers()
    {
        var a = new MavenMetadata("g", "a", ["1.0"], "1.0", "1.0", "20260515130000");
        var b = new MavenMetadata("g", "a", ["2.0"], "2.0", "2.0", "20260515120000");
        var c = new MavenMetadata("g", "a", ["3.0"], "3.0", "3.0", "20260515140000");

        var merged = MetadataMergeService.Merge([a, b, c]);
        merged.LastUpdated.ShouldBe("20260515140000");
    }

    // ── SerializeMetadata ─────────────────────────────────────────────────────

    [Fact]
    public void SerializeMetadata_ContainsVersionElements()
    {
        var m = new MavenMetadata("com.example", "foo", ["1.0", "2.0"], "2.0", "2.0", "20260515120000");
        var xml = MetadataMergeService.SerializeMetadata(m);

        xml.ShouldContain("<version>1.0</version>");
        xml.ShouldContain("<version>2.0</version>");
        xml.ShouldContain("<latest>2.0</latest>");
        xml.ShouldContain("<release>2.0</release>");
        xml.ShouldContain("<lastUpdated>20260515120000</lastUpdated>");
    }

    [Fact]
    public void SerializeMetadata_RoundTrip_PreservesVersions()
    {
        var original = new MavenMetadata("g", "a", ["1.0", "1.5", "2.0"], "2.0", "2.0", "20260515120000");
        var xml      = MetadataMergeService.SerializeMetadata(original);
        var parsed   = MetadataMergeService.ParseMetadata(xml);

        parsed.ShouldNotBeNull();
        parsed.Versions.ShouldBe(original.Versions);
        parsed.Latest.ShouldBe(original.Latest);
        parsed.Release.ShouldBe(original.Release);
        parsed.LastUpdated.ShouldBe(original.LastUpdated);
    }

    // ── MergeMetadataAsync — HTTP integration ─────────────────────────────────

    [Fact]
    public async Task MergeMetadataAsync_AllMembers404_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(new Dictionary<string, HttpResponseMessage>());
        var svc     = BuildService(handler);

        var result = await svc.MergeMetadataAsync(
            ["http://member1", "http://member2"],
            "com/example/foo/maven-metadata.xml",
            CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task MergeMetadataAsync_OneMemberReturnsXml_ReturnsThatXml()
    {
        const string xml = """
            <metadata>
              <groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>1.0</version></versions>
                <latest>1.0</latest><release>1.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["http://member1/com/example/foo/maven-metadata.xml"] = OkXml(xml),
        };

        var svc    = BuildService(new FakeHttpMessageHandler(responses));
        var result = await svc.MergeMetadataAsync(
            ["http://member1", "http://member2"],
            "com/example/foo/maven-metadata.xml",
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain("<version>1.0</version>");
    }

    [Fact]
    public async Task MergeMetadataAsync_TwoMembersWithDifferentVersions_MergesCorrectly()
    {
        const string xmlA = """
            <metadata>
              <groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>1.0</version></versions>
                <latest>1.0</latest><release>1.0</release>
                <lastUpdated>20260515120000</lastUpdated>
              </versioning>
            </metadata>
            """;

        const string xmlB = """
            <metadata>
              <groupId>g</groupId><artifactId>a</artifactId>
              <versioning>
                <versions><version>2.0</version></versions>
                <latest>2.0</latest><release>2.0</release>
                <lastUpdated>20260515130000</lastUpdated>
              </versioning>
            </metadata>
            """;

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["http://member1/com/example/foo/maven-metadata.xml"] = OkXml(xmlA),
            ["http://member2/com/example/foo/maven-metadata.xml"] = OkXml(xmlB),
        };

        var svc    = BuildService(new FakeHttpMessageHandler(responses));
        var result = await svc.MergeMetadataAsync(
            ["http://member1", "http://member2"],
            "com/example/foo/maven-metadata.xml",
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain("<version>1.0</version>");
        result.ShouldContain("<version>2.0</version>");
        result.ShouldContain("<latest>2.0</latest>");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static MetadataMergeService BuildService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new MetadataMergeService(client, NullLogger<MetadataMergeService>.Instance);
    }

    private static HttpResponseMessage OkXml(string xml) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };

    /// <summary>
    /// Minimal fake HTTP handler that returns pre-configured responses by URL.
    /// All other URLs return 404.
    /// </summary>
    private sealed class FakeHttpMessageHandler(Dictionary<string, HttpResponseMessage> responses)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (responses.TryGetValue(url, out var response))
                return Task.FromResult(response);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

