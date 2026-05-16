using MavenOperator.VirtualProxy.Services;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// ── Configuration — mounted ConfigMap at /app/config/appsettings.json ─────────
builder.Configuration.AddJsonFile("/app/config/appsettings.json", optional: false);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<MetadataMergeService>();
builder.Services.AddSingleton<IMetadataMergeService, MetadataMergeService>();
builder.Services.AddSingleton<IVirtualProxyMetrics, VirtualProxyMetrics>();
builder.Services.AddSingleton(sp =>
    builder.Configuration.GetSection("VirtualRepo").Get<VirtualRepoConfig>()
    ?? new VirtualRepoConfig());
builder.Services.AddSingleton<IVirtualProxyService, VirtualProxyService>(sp =>
{
    var config     = sp.GetRequiredService<VirtualRepoConfig>();
    var merge      = sp.GetRequiredService<IMetadataMergeService>();
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var logger     = sp.GetRequiredService<ILogger<VirtualProxyService>>();
    var cache      = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
    var metrics    = sp.GetRequiredService<IVirtualProxyMetrics>();
    return new VirtualProxyService(config, merge, httpClient, logger, cache, metrics);
});

var app = builder.Build();

// ── Prometheus metrics ────────────────────────────────────────────────────────
app.UseMetricServer("/metrics");
app.UseHttpMetrics();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok("OK"));

// Block PUT/DELETE with 405 — Virtual repos are read-only
app.MapPut("/{**path}", () => Results.StatusCode(405));
app.MapDelete("/{**path}", () => Results.StatusCode(405));

app.MapGet("/{**path}", async (string path, IVirtualProxyService proxy, IVirtualProxyMetrics metrics, CancellationToken ct) =>
{
    var assetType = VirtualProxyMetrics.ClassifyAssetType(path);
    var result = await proxy.ForwardAsync(path, ct);
    if (result is null)
    {
        metrics.RecordRequest(app.Configuration["VirtualRepo:Name"] ?? "unknown", path, assetType, 404);
        return Results.NotFound();
    }

    metrics.RecordRequest(app.Configuration["VirtualRepo:Name"] ?? "unknown", path, assetType, 200);
    return Results.Stream(result.Content, result.ContentType);
});

await app.RunAsync();

