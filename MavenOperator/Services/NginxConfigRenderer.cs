using MavenOperator.Entities.Spec;
using Scriban;

namespace MavenOperator.Services;

/// <summary>
/// Renders NGINX configuration files from Scriban templates embedded in the assembly.
/// Pure, stateless, and synchronous — easy to unit-test without any cluster dependency.
/// </summary>
public interface INginxConfigRenderer
{
    /// <summary>Renders the NGINX configuration for a Hosted repository.</summary>
    string RenderHosted(string name, AuthPolicy downloadPolicy, AuthPolicy uploadPolicy);

    /// <summary>
    /// Renders the NGINX configuration for a Proxy repository.
    /// </summary>
    /// <param name="name">Repository name.</param>
    /// <param name="downloadPolicy">Download auth policy.</param>
    /// <param name="upstreamUrl">Base URL of the remote upstream, e.g. https://repo1.maven.org/maven2</param>
    /// <param name="cacheTtl">NGINX cache TTL for 200 responses, e.g. "1d".</param>
    /// <param name="upstreamAuthHeader">
    /// If the upstream requires auth, the full value of the Authorization header to forward
    /// (e.g. "Basic dXNlcjpwYXNz"). Empty string means no upstream auth.
    /// </param>
    string RenderProxy(
        string name,
        AuthPolicy downloadPolicy,
        string upstreamUrl,
        string cacheTtl,
        string upstreamAuthHeader);
}

/// <inheritdoc/>
public sealed class NginxConfigRenderer : INginxConfigRenderer
{
    // Templates are loaded once and cached — Template.Parse is thread-safe after parsing.
    private static readonly Template HostedTemplate = LoadTemplate("nginx-hosted.conf.scriban");
    private static readonly Template ProxyTemplate  = LoadTemplate("nginx-proxy.conf.scriban");

    private static Template LoadTemplate(string fileName)
    {
        var assembly = typeof(NginxConfigRenderer).Assembly;
        // Template files are embedded as resources; the resource name mirrors the folder path.
        var resourceName = $"MavenOperator.Templates.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded template '{resourceName}' not found. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var template = Template.Parse(text);
        if (template.HasErrors)
            throw new InvalidOperationException(
                $"Template '{fileName}' has parse errors: {string.Join("; ", template.Messages)}");

        return template;
    }

    /// <inheritdoc/>
    public string RenderHosted(string name, AuthPolicy downloadPolicy, AuthPolicy uploadPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var result = HostedTemplate.Render(new
        {
            name            = name,
            download_policy = downloadPolicy.ToString(),
            upload_policy   = uploadPolicy.ToString(),
        }, member => member.Name);

        return result;
    }

    /// <inheritdoc/>
    public string RenderProxy(
        string name,
        AuthPolicy downloadPolicy,
        string upstreamUrl,
        string cacheTtl,
        string upstreamAuthHeader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamUrl);

        // Split the upstream URL into scheme+host (for the proxy_pass variable, so NGINX
        // resolves the hostname at request time via the resolver directive) and the path
        // component (which must be part of the rewrite target, because NGINX ignores the
        // path portion of a variable-based proxy_pass directive).
        var uri = new Uri(upstreamUrl.TrimEnd('/'));
        var upstreamSchemeHost = $"{uri.Scheme}://{uri.Authority}";
        var upstreamPath       = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath.TrimEnd('/');

        var result = ProxyTemplate.Render(new
        {
            name                  = name,
            download_policy       = downloadPolicy.ToString(),
            upstream_scheme_host  = upstreamSchemeHost,
            upstream_path         = upstreamPath,
            cache_ttl             = string.IsNullOrWhiteSpace(cacheTtl) ? "1d" : cacheTtl,
            upstream_auth_header  = upstreamAuthHeader ?? string.Empty,
        }, member => member.Name);

        return result;
    }
}

