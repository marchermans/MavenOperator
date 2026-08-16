namespace MavenOperator.Entities.Spec;

/// <summary>
/// Configuration for Virtual repository fan-out.
/// </summary>
public sealed class VirtualSpec
{
    /// <summary>
    /// Ordered list of MavenRepository names that make up this virtual group.
    /// Requests are tried in order for non-metadata artifacts; metadata is merged from all.
    /// Must contain at least one member. Must not contain this repository's own name.
    /// </summary>
    public List<string> Members { get; set; } = [];

    /// <summary>
    /// How long merged maven-metadata.xml responses are cached in-process.
    /// Defaults to 60 seconds.
    /// </summary>
    public int MetadataCacheTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Upload fan-out configuration for Virtual repositories.
    /// When configured with targets, uploads are distributed to declared members in parallel.
    /// Defaults to disabled (empty targets list → 405 on writes).
    /// </summary>
    public VirtualUploadSpec Upload { get; set; } = new();
}

/// <summary>
/// A single upload target for a Virtual repository.
/// Must reference a member declared in spec.virtual.members[].
/// </summary>
public sealed class VirtualUploadTarget
{
    /// <summary>
    /// Name of the member repository to upload to.
    /// Must match an entry in spec.virtual.members[].name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional per-target credentials override.
    /// If not set, falls back to the shared uploadCredentialsRef on VirtualUploadSpec.
    /// </summary>
    public LocalObjectReference? CredentialsRef { get; set; }
}

/// <summary>
/// Upload fan-out configuration for Virtual repositories.
/// </summary>
public sealed class VirtualUploadSpec
{
    /// <summary>
    /// List of member repositories that receive uploads.
    /// Empty list means uploads are disabled (405).
    /// Hosted members store directly; Proxy members forward to their upstream.
    /// Proxy targets require spec.upstream.upload.enabled == true on the target repo.
    /// </summary>
    public List<VirtualUploadTarget> Targets { get; set; } = [];

    /// <summary>
    /// Shared credentials used for all upload targets that don't have their own CredentialsRef.
    /// Required if any target doesn't specify its own credentials.
    /// </summary>
    public LocalObjectReference? SharedCredentialsRef { get; set; }
}

