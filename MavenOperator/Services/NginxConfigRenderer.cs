using MavenOperator.Entities.Spec;
using Scriban;

namespace MavenOperator.Services;

/// <summary>
/// Model passed to the Hosted NGINX Scriban template.
/// </summary>
public sealed record NginxHostedModel(
    string Name,
    string DownloadPolicy,
    string UploadPolicy);

/// <summary>
/// Renders NGINX configuration files from Scriban templates embedded in the assembly.
/// Pure, stateless, and synchronous — easy to unit-test without any cluster dependency.
/// </summary>
public interface INginxConfigRenderer
{
    /// <summary>
    /// Renders the NGINX configuration for a Hosted repository.
    /// </summary>
    string RenderHosted(string name, AuthPolicy downloadPolicy, AuthPolicy uploadPolicy);
}

/// <inheritdoc/>
public sealed class NginxConfigRenderer : INginxConfigRenderer
{
    // Template is loaded once and cached — Template.Parse is thread-safe after parsing.
    private static readonly Template HostedTemplate = LoadTemplate("nginx-hosted.conf.scriban");

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
}

