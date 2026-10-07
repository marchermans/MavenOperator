using k8s;
using k8s.Models;
using MavenOperator.ImportJob.Models;
using System.Text.Json;

namespace MavenOperator.ImportJob.Services;

/// <summary>
/// Reports import progress back to the parent MavenRepositoryImport CR
/// via the Kubernetes API. Progress is written as annotations on the Job object.
/// </summary>
public sealed class ProgressReporter
{
    private readonly IKubernetes? _kubernetes;
    private readonly string _namespace;
    private readonly string _importCrName;
    private readonly string _jobName;
    private readonly ILogger<ProgressReporter> _logger;

    // Throttle updates — don't patch on every artifact
    private long _lastReportedCopied;
    private const int ReportEveryN = 10;

    public ProgressReporter(
        IKubernetes? kubernetes,
        string ns,
        string importCrName,
        string jobName,
        ILogger<ProgressReporter> logger)
    {
        _kubernetes   = kubernetes;
        _namespace    = ns;
        _importCrName = importCrName;
        _jobName      = jobName;
        _logger       = logger;
    }

    /// <summary>
    /// Updates the Job annotations with current progress counters.
    /// Throttled — only patches Kubernetes every ReportEveryN artifacts, but
    /// always reports once when processing reaches its end (copied+failed ==
    /// discovered) so the final counters are persisted before the pod exits.
    /// </summary>
    public async Task ReportAsync(ImportResult progress, CancellationToken ct)
    {
        if (_kubernetes is null) return;

        var done = progress.ArtifactsCopied + progress.ArtifactsFailed == progress.ArtifactsDiscovered;
        if (progress.ArtifactsCopied - _lastReportedCopied < ReportEveryN && !done)
            return;

        _lastReportedCopied = progress.ArtifactsCopied;

        try
        {
            var patch = new
            {
                metadata = new
                {
                    annotations = new Dictionary<string, string>
                    {
                        ["maven.operator.io/artifacts-discovered"] = progress.ArtifactsDiscovered.ToString(),
                        ["maven.operator.io/artifacts-copied"]     = progress.ArtifactsCopied.ToString(),
                        ["maven.operator.io/artifacts-failed"]     = progress.ArtifactsFailed.ToString(),
                        ["maven.operator.io/bytes-transferred"]    = progress.BytesTransferred.ToString(),
                    },
                },
            };

            var patchStr  = JsonSerializer.Serialize(patch);
            var patchBody = new V1Patch(patchStr, V1Patch.PatchType.MergePatch);

            // The counters live on the Job (not a pod) — pods carry a random
            // suffix and the controller reads annotations off the Job.
            await _kubernetes.BatchV1.PatchNamespacedJobAsync(
                patchBody, _jobName, _namespace, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Progress reporting failed — non-fatal");
        }
    }
}

