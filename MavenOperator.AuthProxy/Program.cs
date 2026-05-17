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

    var (success, role) = await validator.ValidateAsync(
        string.IsNullOrWhiteSpace(authHeader) ? null : authHeader,
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

