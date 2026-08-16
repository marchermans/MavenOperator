using MavenOperator.VirtualProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Net;

namespace MavenOperator.Tests.Unit.VirtualProxy.Services;

/// <summary>
/// Unit tests for <see cref="VirtualUploadService"/>.
/// All HTTP calls are stubbed via a fake handler — no real cluster or network required.
/// </summary>
public sealed class VirtualUploadServiceTests : IDisposable
{
    private readonly FakeHttpMessageHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenNoTargetsConfigured()
    {
        var config = new VirtualUploadConfig { Targets = [] };
        var svc = BuildService(config);
        svc.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenTargetsConfigured()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
            ],
        };
        var svc = BuildService(config);
        svc.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task UploadAsync_NoTargets_Returns502()
    {
        var config = new VirtualUploadConfig { Targets = [] };
        var svc = BuildService(config);

        using var content = new MemoryStream([1, 2, 3]);
        var result = await svc.UploadAsync("com/example/foo/1.0/foo-1.0.jar", content, CancellationToken.None);

        result.StatusCode.ShouldBe(502);
        result.TargetResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task UploadAsync_AllTargetsSucceed_Returns201()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
                new UploadTargetConfig("hosted2", "http://hosted2-svc/repository/hosted2/", null),
            ],
        };

        _handler.Responses["PUT:http://hosted1-svc/repository/hosted1/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.Created);
        _handler.Responses["PUT:http://hosted2-svc/repository/hosted2/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.Created);

        var svc = BuildService(config);
        using var content = new MemoryStream([1, 2, 3]);
        var result = await svc.UploadAsync("com/example/foo/1.0/foo-1.0.jar", content, CancellationToken.None);

        result.StatusCode.ShouldBe(201);
        result.TargetResults.Count.ShouldBe(2);
        result.TargetResults.ShouldAllBe(r => r.Success);
    }

    [Fact]
    public async Task UploadAsync_PartialFailure_Returns207()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
                new UploadTargetConfig("hosted2", "http://hosted2-svc/repository/hosted2/", null),
            ],
        };

        _handler.Responses["PUT:http://hosted1-svc/repository/hosted1/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.Created);
        _handler.Responses["PUT:http://hosted2-svc/repository/hosted2/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var svc = BuildService(config);
        using var content = new MemoryStream([1, 2, 3]);
        var result = await svc.UploadAsync("com/example/foo/1.0/foo-1.0.jar", content, CancellationToken.None);

        result.StatusCode.ShouldBe(207); // Multi-status for partial success.
        result.TargetResults.Count(r => r.Success).ShouldBe(1);
        result.TargetResults.Count(r => !r.Success).ShouldBe(1);
    }

    [Fact]
    public async Task UploadAsync_AllTargetsFail_Returns502()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
            ],
        };

        _handler.Responses["PUT:http://hosted1-svc/repository/hosted1/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var svc = BuildService(config);
        using var content = new MemoryStream([1, 2, 3]);
        var result = await svc.UploadAsync("com/example/foo/1.0/foo-1.0.jar", content, CancellationToken.None);

        result.StatusCode.ShouldBe(502);
        result.TargetResults.ShouldAllBe(r => !r.Success);
    }

    [Fact]
    public async Task UploadAsync_WithAuthHeader_SendsAuthorization()
    {
        var authHeader = "Basic dXNlcjpwYXNz";
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", authHeader),
            ],
        };

        _handler.Responses["PUT:http://hosted1-svc/repository/hosted1/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.Created);

        var svc = BuildService(config);
        using var content = new MemoryStream([1, 2, 3]);
        await svc.UploadAsync("com/example/foo/1.0/foo-1.0.jar", content, CancellationToken.None);

        // Verify auth header was sent.
        _handler.LastRequest.Headers.Authorization.ShouldNotBeNull();
        _handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("dXNlcjpwYXNz");
    }

    [Fact]
    public async Task DeleteAsync_AllTargetsSucceed_Returns201()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
                new UploadTargetConfig("hosted2", "http://hosted2-svc/repository/hosted2/", null),
            ],
        };

        _handler.Responses["DELETE:http://hosted1-svc/repository/hosted1/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.OK);
        _handler.Responses["DELETE:http://hosted2-svc/repository/hosted2/com/example/foo/1.0/foo-1.0.jar"] = new HttpResponseMessage(HttpStatusCode.OK);

        var svc = BuildService(config);
        var result = await svc.DeleteAsync("com/example/foo/1.0/foo-1.0.jar", CancellationToken.None);

        result.StatusCode.ShouldBe(201);
        result.TargetResults.ShouldAllBe(r => r.Success);
    }

    [Fact]
    public async Task DeleteAsync_NoTargets_Returns502()
    {
        var config = new VirtualUploadConfig { Targets = [] };
        var svc = BuildService(config);

        var result = await svc.DeleteAsync("com/example/foo/1.0/foo-1.0.jar", CancellationToken.None);

        result.StatusCode.ShouldBe(502);
        result.TargetResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateDirectoryAsync_AllTargetsSucceed_Returns201()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
            ],
        };

        _handler.Responses["PUT:http://hosted1-svc/repository/hosted1/com/example/foo/1.0/"] = new HttpResponseMessage(HttpStatusCode.Created);

        var svc = BuildService(config);
        var result = await svc.CreateDirectoryAsync("com/example/foo/1.0/", CancellationToken.None);

        result.StatusCode.ShouldBe(201);
    }

    [Fact]
    public async Task UploadAsync_TransientFailure_Returns502WithError()
    {
        var config = new VirtualUploadConfig
        {
            Name = "test-virtual",
            Targets =
            [
                new UploadTargetConfig("hosted1", "http://hosted1-svc/repository/hosted1/", null),
            ],
            RetryAttempts = 0, // No retries for this test.
        };

        _handler.RequestHandler = (request) => throw new System.Net.Http.HttpRequestException("Connection refused");

        var svc = BuildService(config);
        using var content = new MemoryStream([1, 2, 3]);
        var result = await svc.UploadAsync("com/example/foo/1.0/foo-1.0.jar", content, CancellationToken.None);

        // Transient failure should be handled gracefully (not crash).
        result.StatusCode.ShouldBe(502);
        result.TargetResults.ShouldAllBe(r => !r.Success && r.ErrorMessage!.Contains("HttpRequestException"));
    }

    [Fact]
    public void VirtualUploadResult_FromTargets_AllSuccess_Returns201()
    {
        var results = new[]
        {
            new TargetUploadResult("t1", true, 201, null),
            new TargetUploadResult("t2", true, 201, null),
        };

        var aggregated = VirtualUploadResult.FromTargets(results);
        aggregated.StatusCode.ShouldBe(201);
    }

    [Fact]
    public void VirtualUploadResult_FromTargets_PartialSuccess_Returns207()
    {
        var results = new[]
        {
            new TargetUploadResult("t1", true, 201, null),
            new TargetUploadResult("t2", false, 403, "Forbidden"),
        };

        var aggregated = VirtualUploadResult.FromTargets(results);
        aggregated.StatusCode.ShouldBe(207);
    }

    [Fact]
    public void VirtualUploadResult_FromTargets_AllFail_Returns502()
    {
        var results = new[]
        {
            new TargetUploadResult("t1", false, 500, "Error"),
            new TargetUploadResult("t2", false, null, "Timeout"),
        };

        var aggregated = VirtualUploadResult.FromTargets(results);
        aggregated.StatusCode.ShouldBe(502);
    }

    [Fact]
    public void VirtualUploadResult_FromTargets_Empty_Returns502()
    {
        var results = Array.Empty<TargetUploadResult>();
        var aggregated = VirtualUploadResult.FromTargets(results);
        aggregated.StatusCode.ShouldBe(502);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IVirtualUploadService BuildService(VirtualUploadConfig config)
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = null };
        return new VirtualUploadService(config, httpClient, NullLogger<VirtualUploadService>.Instance, new TestMetrics());
    }

    // Simple fake HTTP handler for testing.
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Dictionary<string, HttpResponseMessage> Responses { get; } = [];
        public HttpRequestMessage? LastRequest { get; private set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? RequestHandler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;

            if (RequestHandler is not null)
                return RequestHandler(request);

            var key = $"{request.Method}:{request.RequestUri}";
            if (Responses.TryGetValue(key, out var response))
                return Task.FromResult(response);

            // Default: 404 for unmatched requests.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // Minimal metrics implementation for tests.
    private sealed class TestMetrics : IVirtualProxyMetrics
    {
        public void RecordRequest(string repoName, string artifactPath, string assetType, int statusCode) { }
        public void RecordMemberRequest(string repoName, string memberName, bool success, double durationSeconds) { }
        public void RecordMetadataMerge(string repoName, int memberCount, double durationSeconds) { }
        public void RecordUploadRequest(string repoName, string targetName, bool success, double durationSeconds) { }
    }
}
