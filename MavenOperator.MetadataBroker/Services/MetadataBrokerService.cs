using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MavenOperator.MetadataBroker.Services;

/// <summary>HTTP-ish response payload (framework-free so the service stays unit-testable).</summary>
public sealed class MetadataResponse
{
    public int Status { get; init; } = 200;
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }                 // quoted, e.g. "\"<hex>\""
    public DateTimeOffset? LastModifiedUtc { get; init; }
    /// <summary>Extra response headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();
}

/// <summary>What a maven-metadata.xml path under the repository maps to on disk.</summary>
public enum MetadataKind
{
    /// <summary>&lt;group.../artifactId&gt;/maven-metadata.xml — version listing.</summary>
    Listing,
    /// <summary>&lt;group.../artifactId/&lt;v&gt;-SNAPSHOT&gt;/maven-metadata.xml — snapshot metadata.</summary>
    Snapshot,
    /// <summary>Anything else served (or written) verbatim from disk — out-of-scope passthrough.</summary>
    Passthrough,
}

/// <summary>
/// The metadata broker core. All maven-metadata.xml traffic for one hosted repository is
/// funneled here by the pod's NGINX (repository prefix already stripped):
///
///   GET/HEAD  → fingerprint disk state; serve from cache or regenerate; schedule write-back.
///   PUT/POST  → drain + discard client body, invalidate, acknowledge (201). The stale
///               client bytes are the TOCTOU bug — they never touch disk.
///   DELETE    → same as PUT, acknowledged with 204.
///
/// Disk is the single source of truth; generated content is only ever derived from it.
/// </summary>
public sealed class MetadataBrokerService
{
    private const string MetadataFileName = "maven-metadata.xml";

    private readonly string _repoRoot;
    private readonly int _debounceMs;

    /// <summary>Per-path in-memory cache (fingerprint → body/etag).</summary>
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    /// <summary>Single-flight gate per metadata path.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    // Write-back state — guarded by _wbLock.
    private readonly object _wbLock = new();
    private readonly Dictionary<string, WbState> _writebacks = new();

    private sealed class CacheEntry
    {
        public required string Fingerprint { get; init; }
        public required byte[] Body { get; init; }
        public required string ETag { get; init; }
        public required DateTimeOffset LastUpdatedUtc { get; init; }
    }

    private sealed class WbState
    {
        public bool Running { get; set; }
    }

    public MetadataBrokerService(string repositoryRoot, int debounceMs = 2000)
    {
        _repoRoot = Path.GetFullPath(repositoryRoot);
        _debounceMs = Math.Max(50, debounceMs);
    }

    // ── path safety & classification ────────────────────────────────────────────

    /// <summary>Joins a repository-relative path to the root, rejecting traversal escapes.</summary>
    public bool TryResolve(string relPath, out string fullPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
        {
            fullPath = _repoRoot;
            return false;
        }

        foreach (var segment in relPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is ".." or "." || Path.GetInvalidFileNameChars().Any(segment.Contains))
            {
                fullPath = string.Empty;
                return false;
            }
        }

        var joined = Path.Combine(_repoRoot, relPath);
        var full = Path.GetFullPath(joined);
        if (!full.StartsWith(Path.GetFullPath(_repoRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = full;
        return true;
    }

    /// <summary>Classifies a repository-relative path.</summary>
    public MetadataKind Classify(string relPath)
    {
        if (!relPath.EndsWith(MetadataFileName, StringComparison.Ordinal))
            return MetadataKind.Passthrough;

        var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // [...group...][artifactId]                 [maven-metadata.xml] → listing (len >= 4:
        //   a groupId needs at least one dot, i.e. two directory parts, before the artifact)
        // [...group...][artifactId][version-SNAPSHOT] [maven-metadata.xml] → snapshot (len >= 5 in practice)
        if (segments.Length >= 4 && segments[^2].EndsWith("-SNAPSHOT", StringComparison.Ordinal))
            return MetadataKind.Snapshot;

        return segments.Length >= 4 ? MetadataKind.Listing : MetadataKind.Passthrough;
    }

    // ── reads ─────────────────────────────────────────────────────────────────────

    public Task<MetadataResponse> ReadAsync(string relPath, string? ifNoneMatch) =>
        Classify(relPath) switch
        {
            MetadataKind.Listing or MetadataKind.Snapshot => RegenerateReadAsync(relPath, ifNoneMatch),
            _ => Task.FromResult(PassthroughReadAsync(relPath, ifNoneMatch)),
        };

    private async Task<MetadataResponse> RegenerateReadAsync(string relPath, string? ifNoneMatch)
    {
        if (!TryResolve(relPath, out var fullPath)) return new MetadataResponse { Status = 400 };
        var artifactDirPath = Path.GetDirectoryName(fullPath)!; // dir containing the metadata file

        var gate = _gates.GetOrAdd(relPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            CacheEntry? entry;
            if (_cache.TryGetValue(relPath, out var cached))
            {
                // Fingerprint check: one readdir — catches any writer (dav, import job, tools).
                var fp = CurrentFingerprint(artifactDirPath);
                // GenerateSync can return null if content vanished between the two scans → 404 below.
                entry = fp is not null && fp == cached.Fingerprint ? cached : GenerateSync(relPath, artifactDirPath);
            }
            else
            {
                // First sight of this path: scan once and cache.
                entry = GenerateSync(relPath, artifactDirPath);
            }

            if (entry is null) return new MetadataResponse { Status = 404 };

            // Convergence: the generated view may not yet be on disk (write-back pending).
            ScheduleWriteBack(relPath);
            return BuildReadResponse(entry, ifNoneMatch);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Scans disk once and (re)generates the entry for a classified path; null when nothing to serve.</summary>
    private CacheEntry? GenerateSync(string relPath, string artifactDirPath)
    {
        var dir = new DirectoryInfo(artifactDirPath);
        if (!dir.Exists) return null;

        var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool snapshot = IsSnapshotDir(dir.Name);
        (string groupId, string artifactId, string? versionDirName) = snapshot
            ? (JoinSegments(segments[..^3]), segments[^3], segments[^2])
            : (JoinSegments(segments[..^2]), segments[^2], null);

        var entry = ScanAndRender(dir, snapshot, groupId, artifactId, versionDirName);
        if (entry is not null) _cache[relPath] = entry;
        return entry;
    }

    /// <summary>Current on-disk fingerprint of a metadata dir, or null when it holds no real content.</summary>
    private static string? CurrentFingerprint(string artifactDirPath)
    {
        var dir = new DirectoryInfo(artifactDirPath);
        if (!dir.Exists) return null;

        if (IsSnapshotDir(dir.Name))
        {
            var files = RepositoryScanner.ScanSnapshot(dir);
            return files.Count == 0 ? null : RepositoryScanner.FingerprintSnapshot(files);
        }

        var versions = RepositoryScanner.ScanListing(dir);
        return versions.Count == 0 ? null : RepositoryScanner.Fingerprint(versions.Select(v => (v.Name, v.MtimeUtc)));
    }

    /// <summary>Scans a metadata dir once and renders the entry (fingerprint + body). Null when nothing to serve.</summary>
    private static CacheEntry? ScanAndRender(DirectoryInfo dir, bool snapshot,
        string groupId, string artifactId, string? versionDirName)
    {
        if (snapshot)
        {
            var files = RepositoryScanner.ScanSnapshot(dir);
            if (files.Count == 0) return null;

            var (snapBody, snapUpdated) = MetadataXml.RenderSnapshot(groupId, artifactId, versionDirName!, files);
            return new CacheEntry
            {
                Fingerprint = RepositoryScanner.FingerprintSnapshot(files),
                Body = snapBody,
                ETag = $"\"{Sha256Hex(snapBody)}\"",
                LastUpdatedUtc = snapUpdated,
            };
        }

        var versions = RepositoryScanner.ScanListing(dir);
        if (versions.Count == 0) return null;

        var (body, lastUpdated) = MetadataXml.RenderListing(groupId, artifactId, versions.Select(v => (v.Name, v.MtimeUtc)));
        return new CacheEntry
        {
            Fingerprint = RepositoryScanner.Fingerprint(versions.Select(v => (v.Name, v.MtimeUtc))),
            Body = body,
            ETag = $"\"{Sha256Hex(body)}\"",
            LastUpdatedUtc = lastUpdated,
        };
    }

    private static bool IsSnapshotDir(string dirName) => dirName.EndsWith("-SNAPSHOT", StringComparison.Ordinal);

    // ── writes (ack-only) & delete ───────────────────────────────────────────────

    /// <summary>PUT/POST: drain + discard the client body, invalidate, schedule regeneration.</summary>
    public Task<MetadataResponse> WriteAckAsync(string relPath, Stream requestBody) =>
        Classify(relPath) switch
        {
            MetadataKind.Listing or MetadataKind.Snapshot => AckWriteAsync(relPath, requestBody),
            _ => PassthroughWriteAsync(relPath, requestBody),
        };

    private async Task<MetadataResponse> AckWriteAsync(string relPath, Stream requestBody)
    {
        if (!TryResolve(relPath, out var _)) return new MetadataResponse { Status = 400 };

        // Drain fully so the dav client completes its upload; then discard —
        // persisting these bytes is exactly the race we are removing (D1).
        await requestBody.CopyToAsync(Stream.Null);

        _cache.TryRemove(relPath, out _);
        ScheduleWriteBack(relPath);

        return new MetadataResponse
        {
            Status = 201,
            Headers =
            {
                ["X-Maven-Metadata-Accepted"] = "true",
                ["X-Maven-Metadata-Source"] = "ack-only",
            },
        };
    }

    /// <summary>DELETE: invalidate and acknowledge (204). Regeneration converges the file.</summary>
    public Task<MetadataResponse> DeleteAsync(string relPath) =>
        Classify(relPath) switch
        {
            MetadataKind.Listing or MetadataKind.Snapshot => AckDeleteAsync(relPath),
            _ => PassthroughDeleteAsync(relPath),
        };

    private Task<MetadataResponse> AckDeleteAsync(string relPath)
    {
        if (!TryResolve(relPath, out var _)) return Task.FromResult(new MetadataResponse { Status = 400 });

        _cache.TryRemove(relPath, out _);
        ScheduleWriteBack(relPath); // regenerates (or deletes the file when no versions remain)

        return Task.FromResult(new MetadataResponse
        {
            Status = 204,
            Headers = { ["X-Maven-Metadata-Accepted"] = "true", ["X-Maven-Metadata-Source"] = "ack-only" },
        });
    }

    // ── passthrough (unclassified paths keep today's verbatim behaviour) ────────

    private MetadataResponse PassthroughReadAsync(string relPath, string? ifNoneMatch)
    {
        if (!TryResolve(relPath, out var fullPath)) return new MetadataResponse { Status = 400 };
        if (!File.Exists(fullPath)) return new MetadataResponse { Status = 404 };

        byte[] body;
        DateTime mtimeUtc;
        try
        {
            body = File.ReadAllBytes(fullPath);
            mtimeUtc = File.GetLastWriteTimeUtc(fullPath);
        }
        catch (IOException)
        {
            return new MetadataResponse { Status = 500 };
        }

        var etag = $"\"{Sha256Hex(body)}\"";
        if (MatchesIfNoneMatch(ifNoneMatch, etag))
        {
            return new MetadataResponse { Status = 304, ETag = etag, LastModifiedUtc = mtimeUtc };
        }

        return new MetadataResponse
        {
            Body = body,
            ContentType = "application/xml",
            ETag = etag,
            LastModifiedUtc = mtimeUtc,
            Headers = { ["X-Maven-Metadata-Source"] = "passthrough" },
        };
    }

    private Task<MetadataResponse> PassthroughWriteAsync(string relPath, Stream requestBody) =>
        ClassifyAndPassthrough(relPath, async path =>
        {
            using var ms = new MemoryStream();
            await requestBody.CopyToAsync(ms);
            AtomicWrite(path, ms.ToArray());
        }, null);

    private Task<MetadataResponse> PassthroughDeleteAsync(string relPath) =>
        ClassifyAndPassthrough(relPath, null, static path =>
        {
            if (File.Exists(path)) File.Delete(path);
        });

    /// <summary>Shared guard + response for passthrough writes.</summary>
    private Task<MetadataResponse> ClassifyAndPassthrough(string relPath, Func<string, Task>? asyncAction, Action<string>? syncAction)
    {
        if (!TryResolve(relPath, out var fullPath)) return Task.FromResult(new MetadataResponse { Status = 400 });

        if (asyncAction is not null)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await asyncAction(fullPath);
                }
                catch (IOException)
                {
                    return new MetadataResponse { Status = 500 };
                }
                return new MetadataResponse { Status = 201, Headers = { ["X-Maven-Metadata-Source"] = "passthrough" } };
            });
        }

        try
        {
            syncAction!(fullPath);
            return Task.FromResult(new MetadataResponse { Status = 204, Headers = { ["X-Maven-Metadata-Source"] = "passthrough" } });
        }
        catch (IOException)
        {
            return Task.FromResult(new MetadataResponse { Status = 500 });
        }
    }

    // ── response assembly ───────────────────────────────────────────────────────

    private static bool MatchesIfNoneMatch(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        foreach (var candidate in ifNoneMatch.Split(','))
        {
            var c = candidate.Trim();
            if (c.StartsWith("W/", StringComparison.Ordinal)) c = c[2..];
            if (string.Equals(c, etag, StringComparison.Ordinal) || string.Equals(c.Trim('"'), etag.Trim('"'), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static MetadataResponse BuildReadResponse(CacheEntry entry, string? ifNoneMatch)
    {
        var headers = new Dictionary<string, string> { ["X-Maven-Metadata-Source"] = "generated" };
        if (MatchesIfNoneMatch(ifNoneMatch, entry.ETag))
            return new MetadataResponse { Status = 304, ETag = entry.ETag, LastModifiedUtc = entry.LastUpdatedUtc, Headers = headers };

        return new MetadataResponse
        {
            Body = entry.Body,
            ContentType = "application/xml",
            ETag = entry.ETag,
            LastModifiedUtc = entry.LastUpdatedUtc,
            Headers = headers,
        };
    }

    // ── debounced atomic write-back ───────────────────────────────────────────────

    /// <summary>Ensures at most one write-back loop runs per path (concurrent calls coalesce).</summary>
    private void ScheduleWriteBack(string relPath)
    {
        lock (_wbLock)
        {
            if (!_writebacks.TryGetValue(relPath, out var state))
            {
                state = new WbState();
                _writebacks[relPath] = state;
            }

            if (state.Running) return; // loop will re-scan disk at write time — it sees the change.
            state.Running = true;
        }

        _ = Task.Run(() => WriteBackLoopAsync(relPath));
    }

    private async Task WriteBackLoopAsync(string relPath)
    {
        const int maxAttempts = 5;
        var target = Path.Combine(_repoRoot, relPath);
        var attempt = 0;

        try
        {
            while (true)
            {
                // Debounce first so bursts of PUTs/reads coalesce into one write.
                await Task.Delay(attempt == 0 ? _debounceMs : (int)(1000 * Math.Pow(2, attempt)));

                if (await TryWriteOnceAsync(relPath, target)) break;

                if (++attempt >= maxAttempts)
                {
                    // Give up with a log; the next read/PUT will reschedule.
                    System.Diagnostics.Debug.WriteLine($"[metadata-broker] write-back gave up for {relPath} after {maxAttempts} attempts");
                    break;
                }
            }
        }
        finally
        {
            lock (_wbLock)
            {
                if (_writebacks.TryGetValue(relPath, out var state)) state.Running = false;
            }
        }
    }

    /// <summary>One regeneration + compare-and-write cycle. Returns true when converged or no longer needed.</summary>
    private async Task<bool> TryWriteOnceAsync(string relPath, string target)
    {
        var gate = _gates.GetOrAdd(relPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!TryResolve(relPath, out _)) { target = Path.Combine(_repoRoot, relPath); return true; } // invalid path — nothing to do

            string? generatedFingerprint;
            byte[]? body = null;
            var artifactDirPath = Path.GetDirectoryName(target)!;

            if (new DirectoryInfo(artifactDirPath).Exists)
            {
                var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                bool snapshot = Classify(relPath) == MetadataKind.Snapshot;
                (string groupId, string artifactId, string? versionDirName) = snapshot
                    ? (JoinSegments(segments[..^3]), segments[^3], segments[^2])
                    : (JoinSegments(segments[..^2]), segments[^2], null);

                var dir = new DirectoryInfo(artifactDirPath);
                if (snapshot)
                {
                    var files = RepositoryScanner.ScanSnapshot(dir);
                    generatedFingerprint = RepositoryScanner.FingerprintSnapshot(files);
                    body = files.Count > 0 ? MetadataXml.RenderSnapshot(groupId, artifactId, versionDirName!, files).Body : null;
                }
                else
                {
                    var versions = RepositoryScanner.ScanListing(dir);
                    generatedFingerprint = RepositoryScanner.Fingerprint(versions.Select(v => (v.Name, v.MtimeUtc)));
                    body = versions.Count > 0 ? MetadataXml.RenderListing(groupId, artifactId, versions.Select(v => (v.Name, v.MtimeUtc))).Body : null;
                }
            }
            else
            {
                generatedFingerprint = null; // no dir at all — nothing to write
            }

            if (body is null)
            {
                // Nothing on disk anymore → remove a stale metadata file, if any.
                if (File.Exists(target)) File.Delete(target);
                _cache.TryRemove(relPath, out _);
                return true;
            }

            // Skip the write when disk already matches — idempotent no-op.
            if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(body!))
                return true;

            AtomicWrite(target, body!);
            _cache[relPath] = new CacheEntry
            {
                Fingerprint = generatedFingerprint!,
                Body = body!,
                ETag = $"\"{Sha256Hex(body!)}\"",
                LastUpdatedUtc = DateTimeOffset.UtcNow, // conservative: only affects Last-Modified header after rewrite
            };
            return true;
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[metadata-broker] write-back failed for {relPath}: {ex.Message}");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // ── small helpers ─────────────────────────────────────────────────────────────

    /// <summary>Writes atomically: temp file in the same directory + rename (POSIX-atomic).</summary>
    public static void AtomicWrite(string targetPath, byte[] data)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Maven &lt;groupId&gt; is the dot-joined path prefix (com/acme → com.acme).</summary>
    private static string JoinSegments(string[] segments) => string.Join('.', segments);

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
