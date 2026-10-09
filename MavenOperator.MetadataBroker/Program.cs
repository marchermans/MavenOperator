using MavenOperator.MetadataBroker;
using MavenOperator.MetadataBroker.Services;

// ── Configuration ─────────────────────────────────────────────────────────────
var repositoryRoot = Environment.GetEnvironmentVariable("REPOSITORY_ROOT")
    ?? "/var/maven/repository";

var builder = WebApplication.CreateBuilder(args);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// ── Kestrel — bind to 127.0.0.1:8091 (no Service port) ──────────────────────
builder.WebHost.ConfigureKestrel(opts =>
    opts.ListenLocalhost(8091));

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MetadataBrokerService>();

var app = builder.Build();

// ── Health probe ──────────────────────────────────────────────────────────────
app.MapGet("/healthz", () => Results.Ok("ok"));

// ── Metadata broker endpoints ─────────────────────────────────────────────────
// The broker receives requests from NGINX at any path ending in maven-metadata.xml.
// NGINX proxies the request to 127.0.0.1:8091; the route pattern captures everything.

// GET / HEAD — generate and serve metadata from disk state.
app.MapGet("/{**path}", async (
        string path,
        MetadataBrokerService broker,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        if (!path.EndsWith("maven-metadata.xml", StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        // Respect cached ETag for conditional GET.
        var eTag = broker.GetCacheETag(path);
        if (eTag is not null && ctx.Request.Headers.IfNoneMatch.Any(m => m == eTag))
            return Results.StatusCode(304);

        var (body, etag) = await broker.GetOrRegenerateAsync(path, ct);
        if (body is null)
            return Results.NotFound();

        ctx.Response.Headers.ETag = etag;
        // Per design: no-cache, must-revalidate (never let clients / upstream caches
        // serve stale metadata).
        ctx.Response.Headers.CacheControl = "no-cache, must-revalidate";

        return Results.Content(body, "application/xml", statusCode: 200);
    })
    .ExcludeFromDescription();

// PUT / POST — drain the body, invalidate cache, return 201 (acknowledgment).
app.MapPut("/{**path}", async (
        string path,
        MetadataBrokerService broker,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        // Drain and discard the request body (upstream nginx's client_max_body_size
        // caps it).  The broker never persists client bytes for metadata.
        await ctx.Request.Body.CopyToAsync(Stream.Null, ct);

        var invalidated = broker.Invalidate(path);
        return Results.StatusCode(invalidated ? 201 : 404);
    })
    .ExcludeFromDescription();

// DELETE — invalidate and return 204.
app.MapDelete("/{**path}", async (
        string path,
        MetadataBrokerService broker) =>
    {
        broker.Invalidate(path);
        return Results.NoContent();
    })
    .ExcludeFromDescription();

// Note: Kestrel is already configured above to listen on 127.0.0.1:8091.

await app.RunAsync();

// Expose Program class for WebApplicationFactory in tests.
public partial class Program { }

namespace MavenOperator.MetadataBroker
{
    public sealed class MetadataBrokerEntryPoint { }
}
