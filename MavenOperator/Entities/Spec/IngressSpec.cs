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
    /// Defaults to <c>spec.pathPrefix</c> when set, otherwise <c>/repository/{name}</c>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Name of a TLS Secret for HTTPS termination at the Ingress. Optional.
    /// Cannot be combined with <see cref="CertManager"/>.
    /// </summary>
    public string? TlsSecretRef { get; set; }

    /// <summary>
    /// CertManager <c>Certificate</c> issuer configuration for automatic TLS certificate provisioning.
    /// When enabled, the operator creates a <c>Certificate</c> resource and the auto-generated
    /// TLS secret name is used in the Ingress TLS spec.
    /// Cannot be combined with <see cref="TlsSecretRef"/>.
    /// </summary>
    public CertManagerSpec? CertManager { get; set; }

    /// <summary>
    /// Extra annotations merged into the generated Ingress metadata.
    /// Useful for configuring ingress-controller-specific behaviour, e.g.
    /// <c>nginx.ingress.kubernetes.io/rewrite-target</c>.
    /// </summary>
    public Dictionary<string, string> Annotations { get; set; } = new();
}

