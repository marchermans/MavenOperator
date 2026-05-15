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
}

