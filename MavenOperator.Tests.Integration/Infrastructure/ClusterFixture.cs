using k8s;
using System;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;

namespace MavenOperator.Tests.Integration.Infrastructure;

/// <summary>
/// xUnit collection fixture that provides a real Kubernetes client connected to the
/// cluster configured in KUBECONFIG (or in-cluster service account).
/// Each integration test run uses an isolated namespace that is torn down afterwards.
/// </summary>
public sealed class ClusterFixture : IAsyncLifetime
{
    /// <summary>Unique namespace for this test run to prevent pollution.</summary>
    public string Namespace { get; private set; } = string.Empty;

    public IKubernetesClient Client { get; private set; } = null!;

    /// <summary>Raw k8s client for reading custom objects (Gateway API HTTPRoutes).</summary>
    public k8s.IKubernetes Raw { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client     = new KubernetesClient(config);
        Raw       = new k8s.Kubernetes(config);

        Namespace = $"maven-int-{Guid.NewGuid():N}"[..22]; // keep to 63-char DNS limit
        var ns = new V1Namespace { Metadata = new V1ObjectMeta { Name = Namespace } };
        await Client.CreateAsync<V1Namespace>(ns, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await Client.DeleteAsync<V1Namespace>(Namespace, string.Empty, CancellationToken.None);
        }
        catch { /* ignore */ }
    }

    public async Task<V1Secret> CreateCredentialSecretAsync(
        string name, string username, string password)
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = Namespace,
                Labels            = new Dictionary<string, string>
                {
                    ["maven.operator.io/credential"] = "true",
                },
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["username"] = System.Text.Encoding.UTF8.GetBytes(username),
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password),
            },
        };
        return await Client.CreateAsync<V1Secret>(secret, CancellationToken.None);
    }

    public async Task<MavenRepositoryV1Alpha1> CreateHostedRepositoryAsync(
        string name,
        AuthPolicy downloadPolicy = AuthPolicy.Anonymous,
        // Anonymous by default so that bare calls satisfy the CRD CEL rule
        // (Authenticated policies require users or ciTrust).
        AuthPolicy uploadPolicy   = AuthPolicy.Anonymous,
        IEnumerable<string>? uploadSecretRefs   = null,
        IEnumerable<string>? downloadSecretRefs = null,
        string? accessMode = null)
    {
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = Namespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec
                {
                    Size = "1Gi",
                    AccessMode   = accessMode ?? "ReadWriteMany",
                    DeletionPolicy = DeletionPolicy.Delete,
                },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec
                    {
                        Policy     = downloadPolicy,
                        Users = (downloadSecretRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Reader }).ToList(),
                    },
                    Upload = new AuthPolicySpec
                    {
                        Policy     = uploadPolicy,
                        Users = (uploadSecretRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Deployer }).ToList(),
                    },
                },
            },
        };
        return await Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);
    }

    public static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(pollInterval);
        }
        throw new TimeoutException($"Timed out waiting for: {description}");
    }

    // ── Import-test helpers: raw PVCs, one-shot pods, job waits ──────────────

    /// <summary>
    /// Creates a raw ReadWriteOnce PVC in the test namespace (cluster default StorageClass).
    /// NOTE: the default StorageClass uses WaitForFirstConsumer — the PVC only binds once
    /// some pod mounts it, so this does NOT wait for Bound. Use WaitForPvcBoundAsync after
    /// a consumer exists.
    /// </summary>
    public async Task<V1PersistentVolumeClaim> CreateRawPvcAsync(
        string name,
        string size = "1Gi",
        CancellationToken ct = default)
    {
        var pvc = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Namespace },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = new List<string> { "ReadWriteOnce" },
                Resources   = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) },
                },
            }
        };

        await Client.CreateAsync(pvc, ct);
        return pvc;
    }

    /// <summary>Waits until the named PVC is Bound (requires a consumer to exist first).</summary>
    public async Task WaitForPvcBoundAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await WaitUntilAsync(
            async () => (await Client.GetAsync<V1PersistentVolumeClaim>(name, Namespace, ct))!.Status.Phase == "Bound",
            timeout ?? TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(2),
            $"PVC {name} becomes Bound");
    }

    /// <summary>
    /// Runs a one-shot pod (nginx:alpine → busybox sh) that writes the given files into the
    /// mounted PVC under /data and fails if any write fails.
    /// </summary>
    public async Task SeedPvcAsync(
        string pvcName,
        IReadOnlyDictionary<string, string> files,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (var (path, content) in files)
            lines.Add($"mkdir -p \"$(dirname /data/{path})\" && printf %s '{EscapeShellSingleQuotes(content)}' > \"/data/{path}\"");

        await RunOneShotPodAsync(
            podName: $"seed-{Guid.NewGuid():n}"[..12],
            script: string.Join("\n", lines),
            pvcClaimName: pvcName,
            mountReadOnly: false, timeoutSeconds, ct);
    }

    /// <summary>
    /// Runs a one-shot pod asserting the given relative paths exist in the PVC (test -f each).
    /// Mounts read-only so it never conflicts with an RWO writer holding the volume.
    /// </summary>
    public async Task<bool> AssertPvcFilesExistAsync(
        string pvcName,
        IReadOnlyList<string> relativePaths,
        int? expectedFileCount = null,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (var path in relativePaths)
            lines.Add($"test -f \"/data/{path}\"");
        if (expectedFileCount is not null)
            lines.Add($"[ \"$(find /data -type f | wc -l)\" = {expectedFileCount} ]");

        return await RunOneShotPodAsync(
            podName: $"check-{Guid.NewGuid():n}"[..12],
            script: string.Join(" && ", lines),
            pvcClaimName: pvcName,
            mountReadOnly: true, timeoutSeconds, ct);
    }

    /// <summary>Waits until the named Job has >=1 Succeeded pod or exhausted its backoffLimit.</summary>
    public async Task<V1Job> WaitForJobCompletionAsync(
        string jobName,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (DateTime.UtcNow < deadline)
        {
            var job = await Client.GetAsync<V1Job>(jobName, Namespace, ct);
            if (job is not null && job.Status is not null)
            {
                var succeeded = job.Status.Succeeded ?? 0;
                var failed    = job.Status.Failed ?? 0;
                var backoff   = job.Spec.BackoffLimit ?? 6;
                if (succeeded >= 1 || failed >= backoff)
                    return job;
            }

            await Task.Delay(3_000, ct);
        }

        throw new TimeoutException($"Job {jobName} did not reach a terminal state within {(timeout ?? TimeSpan.FromMinutes(5))}");
    }

    private async Task<bool> RunOneShotPodAsync(
        string podName,
        string script,
        string pvcClaimName,
        bool mountReadOnly,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = podName, NamespaceProperty = Namespace },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                Containers = new List<V1Container>
                {
                    new V1Container
                    {
                        Name    = "worker",
                        Image   = "nginx:1.27-alpine",
                        Command = new List<string> { "sh", "-c", script },
                        VolumeMounts = new List<V1VolumeMount>
                        {
                            new V1VolumeMount { Name = "data", MountPath = "/data", ReadOnlyProperty = mountReadOnly }
                        }
                    }
                },
                Volumes = new List<V1Volume>
                {
                    new V1Volume
                    {
                        Name   = "data",
                        PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvcClaimName }
                    }
                }
            }
        };

        await Client.CreateAsync(pod, ct);

        var phase = string.Empty;
        await WaitUntilAsync(
            async () =>
            {
                phase = (await Client.GetAsync<V1Pod>(podName, Namespace, ct))?.Status?.Phase ?? string.Empty;
                return phase is "Succeeded" or "Failed" or "Unknown";
            },
            TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromSeconds(2),
            $"pod {podName} reaches a terminal phase");

        if (phase != "Succeeded")
        {
            var logs = string.Empty;
            try
            {
                using var stream = await Raw.CoreV1.ReadNamespacedPodLogAsync(Namespace, podName, "worker", cancellationToken: ct);
                using var reader = new StreamReader(stream);
                logs = await reader.ReadToEndAsync(ct);
            }
            catch { /* pod may have no log stream — ignore */ }
            throw new InvalidOperationException($"One-shot pod {podName} ended in phase '{phase}' (wanted Succeeded).\nLogs:\n{logs}");
        }

        return true;
    }

    private static string EscapeShellSingleQuotes(string value)
        => value.Replace("'", "'\\''");
}

[CollectionDefinition(ClusterCollection.CollectionName)]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture>
{
    public const string CollectionName = "Cluster";
}

