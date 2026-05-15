# 07 — C# Aggregation Proxy
## Purpose
The C# Aggregation Proxy is a lightweight ASP.NET Core Minimal API application that:
1. Fans out GET requests to Virtual repository members in priority order.
2. Merges `maven-metadata.xml` responses from all members.
3. Blocks PUT/DELETE with `405 Method Not Allowed`.
This runs as a **separate Deployment** per Virtual repository, alongside an NGINX sidecar for TLS + Basic Auth.
---
## Dependencies (NuGet)
| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Caching.Memory` | In-process metadata cache |
| `System.Xml.Linq` | Maven XML parsing (LINQ to XML) |
| `NuGet.Versioning` | Semantic version comparison |
| `Polly` | Retry + timeout on member HTTP calls |
---
## API Endpoints
```
GET /health                    → 200 OK (liveness/readiness)
GET /{**artifactPath}          → proxy or merge
PUT /{**artifactPath}          → 405 Method Not Allowed
DELETE /{**artifactPath}       → 405 Method Not Allowed
```
---
## Configuration (injected by Operator via ConfigMap)
```json
{
  "VirtualRepo": {
    "Name": "public-group",
    "Members": [
      { "Name": "releases",      "BaseUrl": "http://releases-svc/repository/releases" },
      { "Name": "snapshots",     "BaseUrl": "http://snapshots-svc/repository/snapshots" },
      { "Name": "central-proxy", "BaseUrl": "http://central-proxy-svc/repository/central-proxy" }
    ],
    "MetadataCacheTtlSeconds": 60
  }
}
```
---
## Request Handling Pseudocode
```csharp
app.MapGet("/{**path}", async (string path, HttpContext ctx, IProxyService proxy) =>
{
    if (path.EndsWith("maven-metadata.xml"))
        return await proxy.MergeMetadataAsync(path, ctx.RequestAborted);
    return await proxy.ForwardFirstSuccessAsync(path, ctx.RequestAborted);
});
```
### Forward First Success
```csharp
foreach (var member in config.Members)
{
    var response = await httpClient.GetAsync(member.BaseUrl + "/" + path, ct);
    if (response.IsSuccessStatusCode)
        return Results.Stream(await response.Content.ReadAsStreamAsync(ct), response.Content.Headers.ContentType?.ToString());
}
return Results.NotFound();
```
---
## Deployment
The C# proxy is packaged as a Docker image and deployed as part of the MavenOperator solution (multi-project). The operator sets the image reference in the Virtual repo Deployment spec.
The Operator itself can also serve as the aggregation proxy if configured — reducing the number of running containers. However, for MVP, a separate container per Virtual repo is cleaner and provides better isolation.
---
## Future: Streaming Large Artifacts
For MVP, responses are streamed directly using `HttpClient` response streaming — no full buffering in memory. `Results.Stream()` in ASP.NET Core handles this correctly.
