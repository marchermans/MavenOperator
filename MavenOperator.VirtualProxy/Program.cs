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

// Upload service — configured from VirtualRepo.Upload section.
builder.Services.AddHttpClient<VirtualUploadService>();
builder.Services.AddSingleton<IVirtualUploadService>(sp =>
{
    var config   = sp.GetRequiredService<VirtualRepoConfig>();
    var uploadCfg = config.Upload ?? new VirtualUploadConfig();
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var logger     = sp.GetRequiredService<ILogger<VirtualUploadService>>();
    var metrics    = sp.GetRequiredService<IVirtualProxyMetrics>();
    return new VirtualUploadService(uploadCfg, httpClient, logger, metrics);
});

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

var uploadService = app.Services.GetRequiredService<IVirtualUploadService>();

// PUT — upload artifact (fan-out to configured targets, or 405 if disabled)
app.MapPut("/{**path}", async (string path, Stream body, IVirtualProxyMetrics metrics, CancellationToken ct) =>
{
    var repoName = app.Configuration["VirtualRepo:Name"] ?? "unknown";
    var assetType = VirtualProxyMetrics.ClassifyAssetType(path);

    if (!uploadService.IsEnabled)
    {
        metrics.RecordRequest(repoName, path, assetType, 405);
        return Results.StatusCode(405); // Uploads not configured for this virtual repo.
    }

    var result = await uploadService.UploadAsync(path, body, ct);
    metrics.RecordRequest(repoName, path, assetType, result.StatusCode);

    if (result.StatusCode == 201)
        return Results.Created(string.Empty, $"Uploaded to all {result.TargetResults.Count} target(s)");
    if (result.StatusCode == 207)
        return Results.Content(result.Summary, "text/plain", statusCode: 207);

    // 502 — all targets failed.
    var details = string.Join("; ", result.TargetResults.Select(r => $"{r.TargetName}: {r.ErrorMessage ?? "failed"}"));
    return Results.BadRequest($"Upload failed on all targets: {details}");
});

// DELETE — delete artifact (fan-out to configured targets, or 405 if disabled)
app.MapDelete("/{**path}", async (string path, IVirtualProxyMetrics metrics, CancellationToken ct) =>
{
    var repoName = app.Configuration["VirtualRepo:Name"] ?? "unknown";
    var assetType = VirtualProxyMetrics.ClassifyAssetType(path);

    if (!uploadService.IsEnabled)
    {
        metrics.RecordRequest(repoName, path, assetType, 405);
        return Results.StatusCode(405); // Uploads not configured for this virtual repo.
    }

    var result = await uploadService.DeleteAsync(path, ct);
    metrics.RecordRequest(repoName, path, assetType, result.StatusCode);

    if (result.StatusCode == 201)
        return Results.Ok($"Deleted from all {result.TargetResults.Count} target(s)");
    if (result.StatusCode == 207)
        return Results.Content(result.Summary, "text/plain", statusCode: 207);

    var details = string.Join("; ", result.TargetResults.Select(r => $"{r.TargetName}: {r.ErrorMessage ?? "failed"}"));
    return Results.BadRequest($"Delete failed on all targets: {details}");
});

// GET — read artifact (existing behavior unchanged)
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

