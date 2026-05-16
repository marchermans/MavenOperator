using MavenOperator.VirtualProxy.Services;

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
    return new VirtualProxyService(config, merge, httpClient, logger, cache);
});

var app = builder.Build();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok("OK"));

// Block PUT/DELETE with 405 — Virtual repos are read-only
app.MapPut("/{**path}", () => Results.StatusCode(405));
app.MapDelete("/{**path}", () => Results.StatusCode(405));

app.MapGet("/{**path}", async (string path, IVirtualProxyService proxy, CancellationToken ct) =>
{
    var result = await proxy.ForwardAsync(path, ct);
    if (result is null)
        return Results.NotFound();

    return Results.Stream(result.Content, result.ContentType);
});

await app.RunAsync();

