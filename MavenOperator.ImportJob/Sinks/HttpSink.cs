using MavenOperator.ImportJob.Models;
using System.Net.Http.Headers;
using System.Text;

namespace MavenOperator.ImportJob.Sinks;

/// <summary>
/// Uploads artifacts to the NGINX WebDAV endpoint via HTTP PUT.
/// Used as a fallback when the target PVC is ReadWriteOnce and already claimed.
/// </summary>
public sealed class HttpSink : IRepositorySink
{
    private readonly HttpClient _http;
    private readonly string _targetUrl;
    private readonly bool _dryRun;
    private readonly ILogger<HttpSink> _logger;

    public HttpSink(
        IHttpClientFactory httpFactory,
        string targetUrl,
        string? username,
        string? password,
        bool dryRun,
        ILogger<HttpSink> logger)
    {
        _http      = httpFactory.CreateClient("http-sink");
        _targetUrl = targetUrl.TrimEnd('/');
        _dryRun    = dryRun;
        _logger    = logger;

        if (!string.IsNullOrEmpty(username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    public async Task<long> WriteAsync(ArtifactDescriptor artifact, Stream? content, CancellationToken ct)
    {
        if (_dryRun)
        {
            _logger.LogInformation("[DryRun] Would PUT: {Path}", artifact.RelativePath);
            return 0;
        }

        if (content is null)
        {
            _logger.LogWarning("No content for HTTP PUT of {Path} — skipping", artifact.RelativePath);
            return 0;
        }

        var url = $"{_targetUrl}/{artifact.RelativePath}";
        _logger.LogDebug("PUT {Url}", url);

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("PUT {Url} returned {Status}", url, response.StatusCode);
            return 0;
        }

        return artifact.SizeBytes ?? 0;
    }

    public async Task<bool> ExistsAsync(ArtifactDescriptor artifact, CancellationToken ct)
    {
        var url = $"{_targetUrl}/{artifact.RelativePath}";
        try
        {
            var response = await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, url), ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

