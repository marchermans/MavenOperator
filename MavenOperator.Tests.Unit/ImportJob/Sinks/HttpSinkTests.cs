using System.Net;
using System.Text;
using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using MavenOperator.Tests.Unit.Infrastructure;
using NSubstitute;
using Shouldly;
using Xunit;

namespace MavenOperator.Tests.Unit.ImportJob.Sinks;

public sealed class HttpSinkTests
{
    private const string TargetUrl = "http://target-nginx:80";

    private static (HttpSink Sink, ScriptedHttpHandler Handler) BuildSink(
        string? username = null,
        string? password = null,
        bool dryRun = false)
    {
        var handler = new ScriptedHttpHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("http-sink").Returns(new HttpClient(handler));

        return (new HttpSink(
                    factory, TargetUrl, username, password, dryRun,
                    NullLogger<HttpSink>.Instance),
                handler);
    }

    private static ArtifactDescriptor Artifact(string path = "com/example/lib/1.0/lib-1.0.jar", long size = 42) =>
        new() { RelativePath = path, SizeBytes = size };

    [Fact]
    public async Task WriteAsync_PutsContentToTargetUrl()
    {
        var (sink, handler) = BuildSink();
        byte[]? capturedBody = null;
        string? capturedContentType = null;

        handler.WhenExact($"{TargetUrl}/com/example/lib/1.0/lib-1.0.jar", async req =>
        {
            capturedBody = await req.Content.ReadAsByteArrayAsync();
            capturedContentType = req.Content.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        const string payload = "jar-bytes";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var written = await sink.WriteAsync(Artifact(), stream, CancellationToken.None);

        written.ShouldBe(42); // returns declared size on success
        capturedBody!.ShouldBeEquivalentTo(Encoding.UTF8.GetBytes(payload));
        capturedContentType.ShouldBe("application/octet-stream");
        handler.Requests[0].Method.Method.ShouldBe("PUT");
    }

    [Fact]
    public async Task WriteAsync_ReturnsZero_OnNonSuccessStatus()
    {
        var (sink, handler) = BuildSink();
        handler.WhenUrlContains("/com/example/", new HttpResponseMessage(HttpStatusCode.Forbidden));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        (await sink.WriteAsync(Artifact(), stream, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAsync_SkipsWhenNoContent()
    {
        var (sink, handler) = BuildSink();

        (await sink.WriteAsync(Artifact(), content: null, CancellationToken.None)).ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteAsync_DryRun_MakesNoRequests()
    {
        var (sink, handler) = BuildSink(dryRun: true);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));

        (await sink.WriteAsync(Artifact(), stream, CancellationToken.None)).ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistsAsync_TrueOnHead200()
    {
        var (sink, handler) = BuildSink();
        handler.WhenExact($"{TargetUrl}/com/example/lib/1.0/lib-1.0.jar", new HttpResponseMessage(HttpStatusCode.OK));

        (await sink.ExistsAsync(Artifact(), CancellationToken.None)).ShouldBeTrue();
        handler.Requests[0].Method.Method.ShouldBe("HEAD");
    }

    [Fact]
    public async Task ExistsAsync_FalseOn404OrError()
    {
        var (sink, _) = BuildSink();
        // No route registered → 404 from the handler.
        (await sink.ExistsAsync(Artifact(), CancellationToken.None)).ShouldBeFalse();

        var (sink2, handler2) = BuildSink();
        handler2.WhenUrlContains("/com/example/", new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await sink2.ExistsAsync(Artifact(), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Constructor_SetsBasicAuthHeader_WhenUsernameProvided()
    {
        var (sink, handler) = BuildSink(username: "deployer", password: "pw");
        handler.WhenExact($"{TargetUrl}/com/example/lib/1.0/lib-1.0.jar", new HttpResponseMessage(HttpStatusCode.Created));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));

        await sink.WriteAsync(Artifact(), stream, CancellationToken.None);

        var auth = handler.Requests[0].Headers.Authorization;
        auth!.Scheme.ShouldBe("Basic");
        auth.Parameter.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("deployer:pw")));
    }
}
