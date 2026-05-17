using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// Shared pod-readiness polling used by all E2E fixtures.
/// Centralises timeout constants and diagnostic output so failures
/// produce useful logs instead of bare "timed out" messages.
/// </summary>
internal static class PodReadinessHelper
{
    /// <summary>
    /// Seconds to wait for a Service object to appear after creating a CRD.
    /// The operator needs time to reconcile and create child resources.
    /// Raised to 180 s to account for image-pull warm-up on cold CI runners.
    /// </summary>
    public const int ServiceTimeoutSeconds = 180;

    /// <summary>
    /// Seconds to wait for ALL containers in an NGINX pod to become Ready.
    /// When metrics sidecars (nginx-exporter, mtail) are enabled the pod has
    /// three containers; pulling those images on first run can take 60-90 s.
    /// </summary>
    public const int PodReadyTimeoutSeconds = 300;

    /// <summary>
    /// Polling interval between readiness checks.
    /// </summary>
    public const int PollIntervalMs = 2_000;

    /// <summary>
    /// Waits up to <see cref="ServiceTimeoutSeconds"/> for a Service to appear,
    /// then up to <see cref="PodReadyTimeoutSeconds"/> for at least one pod
    /// with all containers Ready.
    ///
    /// On timeout the method throws a <see cref="TimeoutException"/> that
    /// includes a diagnostic snapshot of the relevant pods.
    /// </summary>
    public static async Task WaitForNginxReadyAsync(
        IKubernetesClient client,
        string namespaceName,
        string repoName,
        CancellationToken ct = default)
    {
        // ── Step 1: wait for the Service ────────────────────────────────────
        var svcName  = $"{repoName}-svc";
        var deadline = DateTime.UtcNow.AddSeconds(ServiceTimeoutSeconds);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var svc = await client.GetAsync<V1Service>(svcName, namespaceName, ct);
                if (svc is not null)
                    goto serviceFound;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* not yet */ }
            await Task.Delay(PollIntervalMs, ct);
        }

        throw new TimeoutException(
            $"Service '{svcName}' in '{namespaceName}' was not created within " +
            $"{ServiceTimeoutSeconds} s. Is the operator running and watching this namespace?");

        serviceFound:

        // ── Step 2: wait for pod Ready ───────────────────────────────────────
        deadline = DateTime.UtcNow.AddSeconds(PodReadyTimeoutSeconds);
        var labelSel = $"app={repoName}-nginx";

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var pods = await client.ListAsync<V1Pod>(
                    namespaceName,
                    labelSelector: labelSel,
                    cancellationToken: ct);

                var ready = pods.Any(IsFullyReady);
                if (ready) return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* API not yet serving */ }

            await Task.Delay(PollIntervalMs, ct);
        }

        // ── Timeout — collect diagnostic info ────────────────────────────────
        var diagnosis = await BuildDiagnosticsAsync(client, namespaceName, labelSel, repoName, ct);
        throw new TimeoutException(
            $"NGINX pod for '{repoName}' did not become fully Ready within " +
            $"{PodReadyTimeoutSeconds} s.\n{diagnosis}");
    }

    /// <summary>
    /// Returns true when a pod has at least one container status, is not in a
    /// terminal phase (Failed/Succeeded), and every container reports Ready.
    /// </summary>
    private static bool IsFullyReady(V1Pod pod)
    {
        var phase = pod.Status?.Phase;
        if (phase is "Failed" or "Succeeded" or "Unknown") return false;

        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null || statuses.Count == 0) return false;

        return statuses.All(cs => cs.Ready);
    }

    private static async Task<string> BuildDiagnosticsAsync(
        IKubernetesClient client,
        string namespaceName,
        string labelSel,
        string repoName,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var pods = await client.ListAsync<V1Pod>(namespaceName, labelSelector: labelSel, cancellationToken: ct);
            if (pods.Count == 0)
            {
                sb.AppendLine($"  No pods found with selector '{labelSel}' in namespace '{namespaceName}'.");
                sb.AppendLine("  The operator may not have created the Deployment, or the pod is not scheduled.");
            }
            else
            {
                foreach (var pod in pods)
                {
                    sb.AppendLine($"  Pod: {pod.Metadata.Name}  Phase={pod.Status?.Phase}");
                    foreach (var cs in pod.Status?.ContainerStatuses ?? [])
                    {
                        var waiting = cs.State?.Waiting;
                        sb.AppendLine($"    Container: {cs.Name,-30} Ready={cs.Ready,-5} " +
                                      $"Restarts={cs.RestartCount} " +
                                      (waiting is not null
                                          ? $"Waiting={waiting.Reason}: {waiting.Message}"
                                          : $"State={(cs.State?.Running is not null ? "Running" : "Terminated")}"));
                    }
                    foreach (var cond in pod.Status?.Conditions ?? [])
                        sb.AppendLine($"    Condition: {cond.Type,-20} Status={cond.Status} Reason={cond.Reason} Message={cond.Message}");
                }
            }

            // Also check events in case of ImagePullBackOff or similar issues
            var k8sConfig = KubernetesClientConfiguration.BuildDefaultConfig();
            using var rawClient = new Kubernetes(k8sConfig);
            var events = await rawClient.CoreV1.ListNamespacedEventAsync(
                namespaceName,
                fieldSelector: $"involvedObject.name={repoName}",
                cancellationToken: ct);
            var recent = events.Items
                .OrderByDescending(e => e.LastTimestamp)
                .Take(5);
            foreach (var ev in recent)
                sb.AppendLine($"  Event [{ev.Type}] {ev.Reason}: {ev.Message} (count={ev.Count})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (Diagnostics collection failed: {ex.Message})");
        }
        return sb.ToString();
    }
}


