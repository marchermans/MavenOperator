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
    string RenderHosted(string name, AuthPolicy downloadPolicy, AuthPolicy uploadPolicy,
        MetricsSpec? metrics = null);

    /// <summary>Renders the NGINX configuration for a Proxy repository.</summary>
    string RenderProxy(
        string name,
        AuthPolicy downloadPolicy,
        string upstreamUrl,
        string cacheTtl,
        string upstreamAuthHeader,
        MetricsSpec? metrics = null);

    /// <summary>
    /// Returns the content of the mtail program ConfigMap (maven.mtail).
    /// Independent of repo type — same program handles both Hosted and Proxy logs.
    /// </summary>
    string RenderMtailConfig();
}

/// <inheritdoc/>
public sealed class NginxConfigRenderer : INginxConfigRenderer
{
    private static readonly Template HostedTemplate = LoadTemplate("nginx-hosted.conf.scriban");
    private static readonly Template ProxyTemplate  = LoadTemplate("nginx-proxy.conf.scriban");
    private static readonly string   MtailProgram   = LoadRaw("maven.mtail");

    private static Template LoadTemplate(string fileName)
    {
        var text = LoadRaw(fileName);
        var template = Template.Parse(text);
        if (template.HasErrors)
            throw new InvalidOperationException(
                $"Template '{fileName}' has parse errors: {string.Join("; ", template.Messages)}");
        return template;
    }

    private static string LoadRaw(string fileName)
    {
        var assembly = typeof(NginxConfigRenderer).Assembly;
        var resourceName = $"MavenOperator.Templates.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <inheritdoc/>
    public string RenderHosted(string name, AuthPolicy downloadPolicy, AuthPolicy uploadPolicy,
        MetricsSpec? metrics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        metrics ??= new MetricsSpec();

        return HostedTemplate.Render(new
        {
            name             = name,
            download_policy  = downloadPolicy.ToString(),
            upload_policy    = uploadPolicy.ToString(),
            metrics_enabled  = metrics.Enabled,
            stub_status_port = metrics.StubStatusPort,
        }, member => member.Name);
    }

    /// <inheritdoc/>
    public string RenderProxy(
        string name,
        AuthPolicy downloadPolicy,
        string upstreamUrl,
        string cacheTtl,
        string upstreamAuthHeader,
        MetricsSpec? metrics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamUrl);
        metrics ??= new MetricsSpec();

        var uri = new Uri(upstreamUrl.TrimEnd('/'));
        var upstreamSchemeHost = $"{uri.Scheme}://{uri.Authority}";
        var upstreamPath       = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath.TrimEnd('/');

        return ProxyTemplate.Render(new
        {
            name                  = name,
            download_policy       = downloadPolicy.ToString(),
            upstream_scheme_host  = upstreamSchemeHost,
            upstream_path         = upstreamPath,
            cache_ttl             = string.IsNullOrWhiteSpace(cacheTtl) ? "1d" : cacheTtl,
            upstream_auth_header  = upstreamAuthHeader ?? string.Empty,
            metrics_enabled       = metrics.Enabled,
            stub_status_port      = metrics.StubStatusPort,
        }, member => member.Name);
    }

    /// <inheritdoc/>
    public string RenderMtailConfig() => MtailProgram;
}
