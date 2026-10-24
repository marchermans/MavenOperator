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
        MetricsSpec? metrics = null, bool downloadAuthProxyEnabled = false, bool uploadAuthProxyEnabled = false,
        string? pathPrefix = null);

    /// <summary>Renders the NGINX configuration for a Proxy repository.</summary>
    /// <param name="groupFilters">
    /// Optional allow-list of group-filter patterns (see <c>UpstreamSpec.GroupFilters</c>).
    /// When non-empty, the repository location becomes a regex location that only matches
    /// allowed artifact paths; everything else under the prefix is denied with 403.
    /// </param>
    string RenderProxy(
        string name,
        AuthPolicy downloadPolicy,
        string upstreamUrl,
        string cacheTtl,
        string upstreamAuthHeader,
        MetricsSpec? metrics = null,
        bool downloadAuthProxyEnabled = false,
        string? pathPrefix = null,
        bool uploadEnabled = false,
        ProxyUploadMode uploadMode = ProxyUploadMode.Passthrough,
        AuthPolicy uploadPolicy = AuthPolicy.Authenticated,
        string upstreamUploadAuthHeader = "",
        IReadOnlyList<string>? groupFilters = null);

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
        MetricsSpec? metrics = null, bool downloadAuthProxyEnabled = false, bool uploadAuthProxyEnabled = false,
        string? pathPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        metrics ??= new MetricsSpec();
        var resolvedPathPrefix = RepositoryPathHelper.ResolvePathPrefix(pathPrefix, name);
        var locationPathPrefix = RepositoryPathHelper.ToLocationPrefix(resolvedPathPrefix);
        var regexPrefix = locationPathPrefix == "/"
            ? "/"
            : System.Text.RegularExpressions.Regex.Escape(locationPathPrefix);

        return HostedTemplate.Render(new
        {
            name             = name,
            // NGINX variable names may only contain [a-zA-Z0-9_].
            // Replace hyphens (and any other invalid chars) with underscores so
            // map/log_format identifiers such as $maven_repo_<var_name> are valid.
            var_name         = ToNginxVarName(name),
            download_policy  = downloadPolicy.ToString(),
            upload_policy    = uploadPolicy.ToString(),
            metrics_enabled  = metrics.Enabled,
            stub_status_port = metrics.StubStatusPort,
            download_auth_proxy_enabled = downloadAuthProxyEnabled,
            upload_auth_proxy_enabled = uploadAuthProxyEnabled,
            path_prefix = resolvedPathPrefix,
            location_path_prefix = locationPathPrefix,
            path_prefix_regex = regexPrefix,
        }, member => member.Name);
    }

    /// <inheritdoc/>
    public string RenderProxy(
        string name,
        AuthPolicy downloadPolicy,
        string upstreamUrl,
        string cacheTtl,
        string upstreamAuthHeader,
        MetricsSpec? metrics = null,
        bool downloadAuthProxyEnabled = false,
        string? pathPrefix = null,
        bool uploadEnabled = false,
        ProxyUploadMode uploadMode = ProxyUploadMode.Passthrough,
        AuthPolicy uploadPolicy = AuthPolicy.Authenticated,
        string upstreamUploadAuthHeader = "",
        IReadOnlyList<string>? groupFilters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamUrl);
        metrics ??= new MetricsSpec();
        var resolvedPathPrefix = RepositoryPathHelper.ResolvePathPrefix(pathPrefix, name);
        var locationPathPrefix = RepositoryPathHelper.ToLocationPrefix(resolvedPathPrefix);
        var regexPrefix = locationPathPrefix == "/"
            ? "/"
            : System.Text.RegularExpressions.Regex.Escape(locationPathPrefix);

        // ── Group filters (optional allow-list) ───────────────────────
        // When present, the repository prefix location becomes a regex location that
        // only matches artifact paths covered by at least one pattern; the renderer
        // additionally emits a 403 fallback for everything else under the prefix.
        string groupFilterRegex = string.Empty;
        string locationDirective = locationPathPrefix;
        if (groupFilters is not null)
        {
            if (groupFilters.Count == 0)
                throw new ArgumentException(
                    "upstream.groupFilters must contain at least one pattern when provided. Omit the field to disable filtering.",
                    nameof(groupFilters));

            var patterns = new List<string>(groupFilters.Count);
            for (int i = 0; i < groupFilters.Count; i++)
            {
                var pattern = groupFilters[i];
                if (string.IsNullOrWhiteSpace(pattern))
                    throw new ArgumentException(
                        $"upstream.groupFilters[{i}] must not be empty.", nameof(groupFilters));
                // NGINX reads a regex location up to the next whitespace — reject any
                // whitespace so a pattern can never silently truncate or corrupt the config line.
                if (pattern.Any(char.IsWhiteSpace))
                    throw new ArgumentException(
                        $"upstream.groupFilters[{i}] contains whitespace, which is not allowed in NGINX regex locations: '{pattern}'.",
                        nameof(groupFilters));
                // Patterns are matched against the path AFTER the repository base prefix.
                if (pattern.StartsWith('/'))
                    throw new ArgumentException(
                        $"upstream.groupFilters[{i}] must not start with '/' — patterns match the artifact path after the repository base prefix: '{pattern}'.",
                        nameof(groupFilters));
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(pattern);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException(
                        $"upstream.groupFilters[{i}] is not a valid regular expression: {ex.Message}",
                        nameof(groupFilters), ex);
                }
                patterns.Add(pattern);
            }

            // Each pattern gets its own non-capturing group so user-level alternation/
            // grouping cannot leak into the outer join, and the whole list is wrapped in a
            // single group so PCRE top-level '|' can never detach the '^' anchor.
            // locationPathPrefix already carries the trailing separator ("/repo/x/", or "/"
            // for the host root), which doubles as the boundary before the coordinate path.
            var alternation = string.Join("|", patterns.Select(p => $"(?:{p})"));
            groupFilterRegex = "^" + System.Text.RegularExpressions.Regex.Escape(locationPathPrefix) + "(?:" + alternation + ")";
            locationDirective = "~ " + groupFilterRegex;
        }

        var uri = new Uri(upstreamUrl.TrimEnd('/'));
        var upstreamSchemeHost = $"{uri.Scheme}://{uri.Authority}";
        // Empty (not "/") so the rendered per-request URL map concatenates to
        // "scheme://host/<path>" without a double slash for bare-host upstreams.
        var upstreamPath       = string.IsNullOrEmpty(uri.AbsolutePath) ? "" : uri.AbsolutePath.TrimEnd('/');

        return ProxyTemplate.Render(new
        {
            name                  = name,
            var_name              = ToNginxVarName(name),
            download_policy       = downloadPolicy.ToString(),
            upstream_scheme_host  = upstreamSchemeHost,
            upstream_path         = upstreamPath,
            cache_ttl             = string.IsNullOrWhiteSpace(cacheTtl) ? "1d" : cacheTtl,
            upstream_auth_header  = upstreamAuthHeader ?? string.Empty,
            metrics_enabled       = metrics.Enabled,
            stub_status_port      = metrics.StubStatusPort,
            download_auth_proxy_enabled = downloadAuthProxyEnabled,
            path_prefix = resolvedPathPrefix,
            location_path_prefix = locationPathPrefix,
            location_directive   = locationDirective,
            group_filter_regex   = groupFilterRegex,
            path_prefix_regex = regexPrefix,
            upload_enabled        = uploadEnabled,
            upload_mode           = uploadMode.ToString(),
            upload_policy         = uploadPolicy.ToString(),
            upstream_upload_auth_header = upstreamUploadAuthHeader ?? string.Empty,
        }, member => member.Name);
    }

    /// <inheritdoc/>
    public string RenderMtailConfig() => MtailProgram;

    /// <summary>
    /// Converts a Kubernetes resource name (which may contain hyphens) into a
    /// string that is safe for use inside an NGINX variable name.
    /// NGINX variable names: [a-zA-Z0-9_] only — hyphens are not allowed.
    /// Example: "e2e-hosted-abc123" → "e2e_hosted_abc123"
    /// </summary>
    private static string ToNginxVarName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
}
