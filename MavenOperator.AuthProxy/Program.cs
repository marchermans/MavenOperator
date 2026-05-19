using MavenOperator.AuthProxy;
using MavenOperator.AuthProxy.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
// Load from /etc/maven-auth/config.json (mounted ConfigMap) with hot-reload.
builder.Configuration
    .AddJsonFile("/etc/maven-auth/config.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.Configure<AuthProxyConfig>(builder.Configuration);
builder.Services.AddOptionsWithValidateOnStart<AuthProxyConfig>();

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<JwksCache>();
builder.Services.AddSingleton<IJwksCache, JwksCache>();
builder.Services.AddSingleton<ITrustEvaluator, TrustEvaluator>();
builder.Services.AddScoped<IAuthValidator, AuthValidator>();

var app = builder.Build();

// ── Endpoints ─────────────────────────────────────────────────────────────────

// Single validation endpoint — used by NGINX auth_request directive.
// Returns:
//   200 + X-Auth-Role header  → allow (NGINX passes the request)
//   401                       → missing/invalid credentials
//   403                       → credentials valid but no matching binding / role
app.MapGet("/auth/validate", async (HttpContext ctx, IAuthValidator validator) =>
{
    // NGINX forwards the original Authorization header as-is
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    var originalUri = ctx.Request.Headers["X-Original-Uri"].ToString();
    var originalMethod = ctx.Request.Headers["X-Original-Method"].ToString();

    var (success, role) = await validator.ValidateAsync(
        string.IsNullOrWhiteSpace(authHeader) ? null : authHeader,
        string.IsNullOrWhiteSpace(originalUri) ? null : originalUri,
        string.IsNullOrWhiteSpace(originalMethod) ? null : originalMethod,
        ctx.RequestAborted);

    if (!success)
    {
        // Return 401 if no credentials were supplied, 403 if they were wrong
        return string.IsNullOrWhiteSpace(authHeader)
            ? Results.StatusCode(401)
            : Results.StatusCode(403);
    }

    ctx.Response.Headers["X-Auth-Role"] = role ?? "reader";
    return Results.Ok();
});

// Health probe
app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

// Expose Program class for WebApplicationFactory in integration tests.
public partial class Program { }

namespace MavenOperator.AuthProxy
{
    /// <summary>
    /// Marker class used as the type parameter for WebApplicationFactory
    /// in integration tests. Avoids the global <c>Program</c> class name
    /// collision when both MavenOperator and MavenOperator.AuthProxy are
    /// referenced in the same test project.
    /// </summary>
    public sealed class AuthProxyEntryPoint { }
}

