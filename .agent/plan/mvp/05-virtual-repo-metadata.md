# 05 — Virtual Repository & maven-metadata.xml Merging
## The Problem
When a Maven client resolves a dependency from a virtual (group) repository it may request:
- `/com/example/foo/maven-metadata.xml` — lists all available versions.
- `/com/example/foo/1.0-SNAPSHOT/maven-metadata.xml` — lists snapshot build numbers/timestamps.
Each member repository may have a **different** `maven-metadata.xml` with non-overlapping version sets. A naive proxy returning the first successful response would hide versions available in other members.
---
## Merge Algorithm
### Release metadata (`/groupId/artifactId/maven-metadata.xml`)
```xml
<!-- member A -->
<metadata>
  <versioning>
    <versions><version>1.0</version><version>1.1</version></versions>
    <latest>1.1</latest>
    <release>1.1</release>
    <lastUpdated>20260515120000</lastUpdated>
  </versioning>
</metadata>
<!-- member B -->
<metadata>
  <versioning>
    <versions><version>2.0</version></versions>
    <latest>2.0</latest>
    <release>2.0</release>
    <lastUpdated>20260515130000</lastUpdated>
  </versioning>
</metadata>
<!-- merged result -->
<metadata>
  <versioning>
    <versions>
      <version>1.0</version><version>1.1</version><version>2.0</version>
    </versions>
    <latest>2.0</latest>   <!-- highest semver -->
    <release>2.0</release> <!-- highest non-SNAPSHOT semver -->
    <lastUpdated>20260515130000</lastUpdated> <!-- latest timestamp -->
  </versioning>
</metadata>
```
### Merge rules
1. **versions**: Union of all member version lists, deduplicated, sorted (semantic version order).
2. **latest**: Highest version by semantic comparison.
3. **release**: Highest non-SNAPSHOT version.
4. **lastUpdated**: Maximum timestamp across all members.
### Snapshot metadata (`/groupId/artifactId/version-SNAPSHOT/maven-metadata.xml`)
Snapshot metadata is typically found only in a single Hosted repo. The merger returns the first successful non-404 response. If multiple members return it (unlikely but possible), it returns the one with the highest `lastUpdated`.
---
## C# Implementation
```csharp
public class MetadataMergeService
{
    // Fetch metadata from all members in parallel
    public async Task<MavenMetadata> MergeAsync(
        IEnumerable<string> memberBaseUrls,
        string path,
        CancellationToken ct)
    {
        var tasks = memberBaseUrls.Select(url => FetchMetadataAsync(url + path, ct));
        var results = await Task.WhenAll(tasks);
        var valid = results.Where(r => r is not null).ToList();
        if (valid.Count == 0) throw new NotFoundException();
        if (valid.Count == 1) return valid[0]!;
        return Merge(valid!);
    }
    private MavenMetadata Merge(List<MavenMetadata> all) { ... }
}
```
### Caching strategy
- Merged metadata is cached in-memory with a configurable TTL (default 60 seconds).
- Cache key: `{virtualRepoName}:{artifactPath}`.
- On cache miss: parallel fetch + merge + store.
- Cache is a simple `IMemoryCache` for MVP; can be replaced with Redis for HA.
---
## Edge Cases
| Case | Handling |
|------|---------|
| All members return 404 | Return 404 to client |
| Some members return 404 | Merge the available responses |
| Network timeout on a member | Log warning, exclude from merge (partial result) |
| Malformed XML from a member | Log error, exclude from merge |
| Version string not semver-parseable | Treat as string, sort lexicographically |
| Circular virtual repo reference | CEL validation on CRD admission prevents this |
