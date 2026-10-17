using Microsoft.Extensions.Logging;
using MavenOperator.MetadataBroker.Services;

// ── Metadata broker sidecar (Maven hosted repositories) ─────────────────────────────
// Pod-internal service: bound to loopback only, reached exclusively through the pod's
// NGINX (which strips the repository prefix and routes maven-metadata.xml here).
// See .agent/plan/maven-metadata/hosted-metadata-broker.md for the full design.

// ── Probe mode ─────────────────────────────────────────────────────────────
// The kubelet runs httpGet/tcSocket probes from OUTSIDE the pod's network
// namespace (against the pod IP), which can never reach a loopback-only bind.
// The operator therefore wires exec probes that run this lightweight client
// *inside* the container, where 127.0.0.1 is reachable:
//   dotnet MavenOperator.MetadataBroker.dll --health-probe
// Exit code: 0 when /healthz answers with a success status, 1 on any failure
// (Kubernetes treats every non-zero exit as probe failure).
if (args.Length > 0 && args[0] == "--health-probe")
{
    var baseUri = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8091";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await http.GetAsync($"{baseUri.TrimEnd('/')}/healthz");
        return resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8091");

var repositoryRoot = Environment.GetEnvironmentVariable("REPOSITORY_ROOT") ?? "/var/maven/repository";
var broker = new MetadataBrokerService(repositoryRoot);
builder.Services.AddSingleton(broker);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

// Repository-relative path, percent-decoded (route values arrive raw).
static string RelPath(HttpRequest req) =>
    Uri.UnescapeDataString(req.Path.Value?.TrimStart('/') ?? "");

static string? IfNoneMatchValue(HttpRequest req) =>
    req.Headers.IfNoneMatch.Count > 0 ? req.Headers.IfNoneMatch.ToString() : null;

// HEAD is served automatically by the GET handler (framework strips the body).
var readHandler = async (MetadataBrokerService b, HttpRequest req) =>
    new BrokerResult(await b.ReadAsync(RelPath(req), IfNoneMatchValue(req)));

app.MapGet("/{**path}", readHandler);

app.MapPut("/{**path}",
    async (MetadataBrokerService b, HttpRequest req) =>
        new BrokerResult(await b.WriteAckAsync(RelPath(req), req.Body)));

app.MapPost("/{**path}",
    async (MetadataBrokerService b, HttpRequest req) =>
        new BrokerResult(await b.WriteAckAsync(RelPath(req), req.Body)));

app.MapDelete("/{**path}",
    async (MetadataBrokerService b, HttpRequest req) =>
        new BrokerResult(await b.DeleteAsync(RelPath(req))));

app.Run();
return 0;

/// <summary>Minimal-API result adapter for the framework-free MetadataResponse.</summary>
file sealed class BrokerResult : IResult
{
    private readonly MetadataResponse _r;

    public BrokerResult(MetadataResponse r) => _r = r;

    public async Task ExecuteAsync(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = _r.Status;

        if (_r.ContentType is not null && _r.Body is not null)
            response.ContentType = _r.ContentType;

        if (_r.ETag is not null)
            response.Headers.ETag = _r.ETag;

        if (_r.LastModifiedUtc is { } lmt)
            response.Headers["Last-Modified"] = lmt.UtcDateTime.ToString("R");

        foreach (var kv in _r.Headers)
            response.Headers[kv.Key] = kv.Value;

        // 304 must not carry a body.
        if (_r.Status != StatusCodes.Status304NotModified && _r.Body is { } body)
            await response.Body.WriteAsync(body);
    }
}
