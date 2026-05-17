using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MavenOperator.ImportJob.Sources;

/// <summary>
/// Crawls a JFrog Artifactory Cloud instance using the Storage REST API.
/// Uses a single deep=1 call to get a flat file list, then downloads each artifact.
/// </summary>
public sealed class JFrogCloudApiSource : IRepositorySource
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _repository;
    private readonly bool _includeSignatures;
    private readonly ILogger<JFrogCloudApiSource> _logger;

    private static readonly string[] SkipFileNames = ["maven-metadata.xml"];

    public JFrogCloudApiSource(
        IHttpClientFactory httpFactory,
        string baseUrl,
        string repository,
        string? token,
        string? username,
        string? password,
        bool includeSignatures,
        ILogger<JFrogCloudApiSource> logger)
    {
        _http              = httpFactory.CreateClient("jfrog");
        _baseUrl           = baseUrl.TrimEnd('/');
        _repository        = repository;
        _includeSignatures = includeSignatures;
        _logger            = logger;

        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (!string.IsNullOrEmpty(username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }
    }

    public async IAsyncEnumerable<ArtifactDescriptor> CrawlAsync(
        ImportFilters filters,
        [EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset? since = filters.SinceTimestamp is { Length: > 0 } ts
            ? DateTimeOffset.Parse(ts)
            : null;

        var url = $"{_baseUrl}/artifactory/api/storage/{_repository}/?list&deep=1&listFolders=0";
        _logger.LogInformation("Fetching JFrog artifact list from {Url}", url);

        ArtifactoryStorageList? listing = null;
        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            listing = JsonSerializer.Deserialize<ArtifactoryStorageList>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch JFrog artifact list");
            yield break;
        }

        if (listing?.Files is null) yield break;

        foreach (var file in listing.Files)
        {
            if (ct.IsCancellationRequested) yield break;

            // Strip leading slash from URI
            var relativePath = file.Uri.TrimStart('/');

            // Skip maven-metadata.xml
            var fileName = Path.GetFileName(relativePath);
            if (SkipFileNames.Any(s => fileName.Equals(s, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Skip .asc GPG signatures unless requested
            if (!_includeSignatures && relativePath.EndsWith(".asc", StringComparison.OrdinalIgnoreCase))
                continue;

            // sinceTimestamp filter
            if (since.HasValue && file.LastModified.HasValue && file.LastModified < since)
                continue;

            // Group filter
            if (!FilterHelper.MatchesGroupFilters(relativePath, filters))
                continue;

            _logger.LogDebug("Found JFrog artifact: {Path}", relativePath);

            yield return new ArtifactDescriptor
            {
                RelativePath = relativePath,
                SizeBytes    = file.Size,
                Sha256       = file.Sha256,
                LastModified = file.LastModified,
            };
        }
    }

    /// <summary>Opens a download stream for an artifact.</summary>
    public async Task<Stream?> OpenStreamAsync(ArtifactDescriptor artifact, CancellationToken ct)
    {
        var url = $"{_baseUrl}/artifactory/{_repository}/{artifact.RelativePath}";
        try
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error downloading JFrog artifact {Path}", artifact.RelativePath);
            return null;
        }
    }

    private sealed class ArtifactoryStorageList
    {
        public List<ArtifactoryFile> Files { get; set; } = [];
    }

    private sealed class ArtifactoryFile
    {
        public string Uri { get; set; } = string.Empty;
        public DateTimeOffset? LastModified { get; set; }
        public long? Size { get; set; }
        public string? Sha1 { get; set; }
        public string? Sha256 { get; set; }
    }
}

