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

public sealed class ReposiliteApiSourceTests
{
    private const string BaseUrl = "http://reposilite.example";
    private const string Repo = "releases";

    private static (ReposiliteApiSource Source, ScriptedHttpHandler Handler) BuildSource(
        string? username = null,
        string? password = null)
    {
        var handler = new ScriptedHttpHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("reposilite").Returns(new HttpClient(handler));

        return (new ReposiliteApiSource(
                    factory, BaseUrl, Repo, username, password,
                    NullLogger<ReposiliteApiSource>.Instance),
                handler);
    }

    private static HttpResponseMessage ListingJson(params (string Name, string Type)[] entries)
    {
        var sb = new StringBuilder("{\"files\":[");
        for (var i = 0; i < entries.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"name\":{JsonString(entries[i].Name)},\"type\":{JsonString(entries[i].Type)}}}");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.Append("]}").ToString()),
        };
    }

    private static string JsonString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [Fact]
    public async Task CrawlAsync_TraversesNestedDirectoriesAndSkipsMetadata()
    {
        var (source, handler) = BuildSource();

        // Root: one directory + a maven-metadata.xml that must be skipped.
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}",
            ListingJson(("com", "DIRECTORY"), ("maven-metadata.xml", "FILE")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/com",
            ListingJson(("example", "DIRECTORY")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/com/example",
            ListingJson(("lib-1.0.jar", "FILE"), ("maven-metadata.xml", "FILE")));

        var artifacts = await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib-1.0.jar");
    }

    [Fact]
    public async Task CrawlAsync_AppliesSinceTimestampFilter()
    {
        var (source, handler) = BuildSource();

        // One old file and one new file in the root directory.
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"files":[
                  {"name":"old-1.0.jar","type":"FILE","lastModified":"2026-01-01T00:00:00Z"},
                  {"name":"new-2.0.jar","type":"FILE","lastModified":"2026-08-01T00:00:00Z"}
                ]}
                """),
        });

        var filters = new ImportFilters { SinceTimestamp = "2026-06-01T00:00:00Z" };
        var artifacts = await source.CrawlAsync(filters, CancellationToken.None).ToListAsync();

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("new-2.0.jar");
    }

    [Fact]
    public async Task CrawlAsync_AppliesGroupFilters()
    {
        var (source, handler) = BuildSource();

        // Proper Maven layout: com/example/lib/1.0/lib-1.0.jar (group = "com.example").
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}",
            ListingJson(("com", "DIRECTORY"), ("org", "DIRECTORY")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/com",
            ListingJson(("example", "DIRECTORY")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/com/example",
            ListingJson(("lib", "DIRECTORY")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/com/example/lib",
            ListingJson(("1.0", "DIRECTORY")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/com/example/lib/1.0",
            ListingJson(("lib-1.0.jar", "FILE")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/org",
            ListingJson(("acme", "DIRECTORY")));
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}/org/acme",
            ListingJson(("tool-1.0.jar", "FILE")));

        var filters = new ImportFilters { IncludeGroups = ["com.example.*"] };
        var artifacts = await source.CrawlAsync(filters, CancellationToken.None).ToListAsync();

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib-1.0.jar");
    }

    [Fact]
    public async Task CrawlAsync_ReturnsEmpty_WhenListingFails()
    {
        var (source, handler) = BuildSource();
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}", new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var artifacts = await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();
        artifacts.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrawlAsync_SetsBasicAuthHeader_WhenUsernameProvided()
    {
        var (source, handler) = BuildSource(username: "admin", password: "s3cret");
        handler.WhenExact($"{BaseUrl}/api/maven/details/{Repo}", ListingJson());

        await source.CrawlAsync(new ImportFilters(), CancellationToken.None).ToListAsync();

        var auth = handler.Requests[0].Headers.Authorization;
        auth.ShouldNotBe(null);
        auth!.Scheme.ShouldBe("Basic");
        auth.Parameter.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:s3cret")));
    }

    [Fact]
    public async Task OpenStreamAsync_DownloadsArtifactBytes()
    {
        var (source, handler) = BuildSource();
        const string bytes = "jar-content";
        handler.WhenExact($"{BaseUrl}/api/maven/repository/{Repo}/com/example/lib-1.0.jar", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bytes)),
        });

        var artifact = new ArtifactDescriptor { RelativePath = "com/example/lib-1.0.jar" };
        await using var stream = (await source.OpenStreamAsync(artifact, CancellationToken.None))!;
        stream.ShouldNotBe(null);

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).ShouldBe(bytes);
    }

    [Fact]
    public async Task OpenStreamAsync_ReturnsNull_On404()
    {
        var (source, handler) = BuildSource();
        handler.WhenUrlContains("/api/maven/repository/", new HttpResponseMessage(HttpStatusCode.NotFound));

        var artifact = new ArtifactDescriptor { RelativePath = "com/example/missing.jar" };
        (await source.OpenStreamAsync(artifact, CancellationToken.None)).ShouldBe(null);
    }
}
