using MavenOperator.ImportJob.Models;

namespace MavenOperator.ImportJob.Sources;

/// <summary>
/// Crawls a source Maven repository and yields artifact descriptors for migration.
/// </summary>
public interface IRepositorySource
{
    /// <summary>
    /// Asynchronously enumerates all artifacts matching the given filters.
    /// </summary>
    IAsyncEnumerable<ArtifactDescriptor> CrawlAsync(
        ImportFilters filters,
        CancellationToken ct);
}

