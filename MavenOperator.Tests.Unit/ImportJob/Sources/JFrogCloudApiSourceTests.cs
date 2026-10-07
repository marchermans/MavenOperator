using System.Net;
using System.Text;
using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Sources;
using MavenOperator.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace MavenOperator.Tests.Unit.ImportJob.Sources;

public sealed class JFrogCloudApiSourceTests
{
    private const string BaseUrl = "https://myorg.jfrog.io"; // source appends /artifactory itself
    private const string Repo = "releases";

    private static (JFrogCloudApiSource Source, ScriptedHttpHandler Handler) BuildSource(
        string? token = null,
        string? username = null,
        string? password = null,
        bool includeSignatures = false)
    {
        var handler = new ScriptedHttpHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("jfrog").Returns(new HttpClient(handler));

        return (new JFrogCloudApiSource(
                    factory, BaseUrl, Repo, token, username, password, includeSignatures,
                    NullLogger<JFrogCloudApiSource>.Instance),
                handler);
    }

    private static HttpResponseMessage StorageListing(params string[] fileJsons) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"files\":[" + string.Join(",", fileJsons) + "]}"),
        };

    [Fact]
    public async Task CrawlAsync_ParsesDeepListingAndStripsLeadingSlash()
    {
        var (source, handler) = BuildSource(token: "jfrog-token");
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", StorageListing(
            """{"uri":"/com/example/lib/1.0/lib-1.0.jar","lastModified":"2026-09-01T10:00:00Z","size":1234,"sha256":"abc123"}"""));

        var artifacts = await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib-1.0.jar");
        artifacts[0].SizeBytes.ShouldBe(1234);
        artifacts[0].Sha256.ShouldBe("abc123");
    }

    [Fact]
    public async Task CrawlAsync_SkipsMetadataAndSignatures_ByDefault()
    {
        var (source, handler) = BuildSource();
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", StorageListing(
            """{"uri":"/com/example/lib/1.0/lib-1.0.jar","size":1234}""",
            """{"uri":"/com/example/lib/maven-metadata.xml","size":80}""",
            """{"uri":"/org/acme/tool/1.0/tool-1.0.jar.asc","size":300,"sha256":"asc"}"""));

        var artifacts = await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib-1.0.jar");
    }

    [Fact]
    public async Task CrawlAsync_IncludesSignatures_WhenRequested()
    {
        var (source, handler) = BuildSource(includeSignatures: true);
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", StorageListing(
            """{"uri":"/org/acme/tool/1.0/tool-1.0.jar","size":77}""",
            """{"uri":"/org/acme/tool/1.0/tool-1.0.jar.asc","size":300,"sha256":"asc"}"""));

        var artifacts = await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        artifacts.Select(a => a.RelativePath)
            .ShouldBe(["org/acme/tool/1.0/tool-1.0.jar", "org/acme/tool/1.0/tool-1.0.jar.asc"], ignoreOrder: true);
    }

    [Fact]
    public async Task CrawlAsync_AppliesSinceTimestampAndGroupFilters()
    {
        var (source, handler) = BuildSource();
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", StorageListing(
            """{"uri":"/com/example/lib/1.0/lib-1.0.jar","lastModified":"2026-08-01T00:00:00Z","size":1}""",
            """{"uri":"/com/example/lib/0.9/old-0.9.jar","lastModified":"2026-01-01T00:00:00Z","size":1}""",
            """{"uri":"/org/acme/tool/1.0/tool-1.0.jar","lastModified":"2026-08-01T00:00:00Z","size":1}"""));

        var filters = new ImportFilters
        {
            SinceTimestamp = "2026-06-01T00:00:00Z",
            IncludeGroups = ["com.example.*"],
        };

        var artifacts = await source.CrawlAsync(filters, CancellationToken.None).ToListAsync();

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib-1.0.jar");
    }

    [Fact]
    public async Task CrawlAsync_ReturnsEmpty_WhenListingFails()
    {
        var (source, handler) = BuildSource();
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", new HttpResponseMessage(HttpStatusCode.Forbidden));

        var artifacts = await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();
        artifacts.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrawlAsync_SetsBearerToken_WhenProvided()
    {
        var (source, handler) = BuildSource(token: "jfrog-token");
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", StorageListing());

        await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        var auth = handler.Requests[0].Headers.Authorization;
        auth!.Scheme.ShouldBe("Bearer");
        auth.Parameter.ShouldBe("jfrog-token");
    }

    [Fact]
    public async Task CrawlAsync_FallsBackToBasicAuth_WhenNoToken()
    {
        var (source, handler) = BuildSource(username: "ci", password: "pw");
        handler.WhenExact($"{BaseUrl}/artifactory/api/storage/{Repo}/", StorageListing());

        await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        var auth = handler.Requests[0].Headers.Authorization;
        auth!.Scheme.ShouldBe("Basic");
        auth.Parameter.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("ci:pw")));
    }

    [Fact]
    public async Task OpenStreamAsync_DownloadsFromArtifactoryPath()
    {
        var (source, handler) = BuildSource(token: "t");
        const string bytes = "pom-content";
        handler.WhenExact($"{BaseUrl}/artifactory/{Repo}/com/example/lib/1.0/pom.xml", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bytes)),
        });

        var artifact = new ArtifactDescriptor { RelativePath = "com/example/lib/1.0/pom.xml" };
        await using var stream = (await source.OpenStreamAsync(artifact, CancellationToken.None))!;

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).ShouldBe(bytes);
    }

    [Fact]
    public async Task OpenStreamAsync_ReturnsNull_On404()
    {
        var (source, handler) = BuildSource(token: "t");
        handler.WhenUrlContains("/artifactory/releases/", new HttpResponseMessage(HttpStatusCode.NotFound));

        var artifact = new ArtifactDescriptor { RelativePath = "com/example/missing.jar" };
        (await source.OpenStreamAsync(artifact, CancellationToken.None)).ShouldBe(null);
    }
}
