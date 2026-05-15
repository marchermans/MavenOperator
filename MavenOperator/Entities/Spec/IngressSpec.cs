namespace MavenOperator.Entities.Spec;

/// <summary>
/// Ingress/exposure configuration for a MavenRepository.
/// </summary>
public sealed class IngressSpec
{
    /// <summary>
    /// Whether to create a Kubernetes Ingress resource for this repository.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Hostname the Ingress should respond on, e.g. "maven.example.com".
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// URL path prefix, e.g. "/repository/my-releases".
    /// Defaults to "/repository/{name}" if not specified.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Name of a TLS Secret for HTTPS termination at the Ingress. Optional.
    /// </summary>
    public string? TlsSecretRef { get; set; }
}

