using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Tests.E2E.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MavenOperator.Tests.E2E.Import;

/// <summary>
/// Phase 7 E2E — PVC snapshot import through the deployed operator.
///
/// Flow:
///   1. Seed a source PVC with two Maven-layout artifacts (one-shot pod).
///   2. Create an anonymous-auth Hosted MavenRepository (target, ReadWriteOnce).
///   3. Submit a MavenRepositoryImport CR with spec.source.pvcSnapshot pointing at
///      the seeded PVC and wait for status.phase == Succeeded.
///   4. Port-forward to the target repository's NGINX service and verify both
///      artifacts are downloadable over HTTP with identical content.
///
/// On the single-node k3d cluster (run-tests.sh default) the import Job mounts
/// both PVCs on the same node, so the controller resolves DirectWrite transfer;
/// on multi-node clusters it falls back to HTTP PUT — either way the artifacts
/// must end up served by NGINX.
/// </summary>
public sealed class PvcSnapshotImportE2ETests : IAsyncLifetime
{
    private const string Ns       = "maven-e2e-import";
    private const string JarPath  = "com/example/lib/1.0/lib-1.0.jar";
    private const string PomPath  = "com/example/lib/1.0/pom.xml";

    private readonly string _suffix;
    private readonly string _sourcePvc;
    private readonly string _seedPod;
    private readonly string _targetRepo;
    private readonly string _importName;

    public PvcSnapshotImportE2ETests()
    {
        _suffix     = Guid.NewGuid().ToString("N")[..8];
        _sourcePvc  = $"imp-src-{_suffix}";
        _seedPod    = $"imp-seed-{_suffix}";
        _targetRepo = $"imp-tgt-{_suffix}";
        _importName = $"imp-e2e-{_suffix}";
    }

    private static readonly byte[] JarBytes = Encoding.UTF8.GetBytes("PK e2e snapshot jar");
    private static readonly byte[] PomBytes = Encoding.UTF8.GetBytes(
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <project><groupId>com.example</groupId><artifactId>lib</artifactId></project>
        """);

    private IKubernetesClient _client;
    private k8s.IKubernetes   _raw;
    private HttpClient?       _http;
    private Process?          _portForward;
    private int               _localPort;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        _client = new KubernetesClient(config);
        _raw    = new k8s.Kubernetes(config);

        await EnsureNamespaceAsync(Ns);
        await CleanupStaleArtifactsAsync();
        await CreateSourcePvcAsync(_sourcePvc, "1Gi");
        await SeedSourcePvcAsync();
        await CreateTargetRepositoryAsync();
        await WaitForTargetReadyAsync();
    }

    public async Task DisposeAsync()
    {
        try { _portForward?.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        _portForward?.Dispose();
        _http?.Dispose();

        foreach (var (kind, name) in new[]
                 {
                     (typeof(MavenRepositoryImportV1Alpha1), _importName),
                     (typeof(MavenRepositoryV1Alpha1), _targetRepo),
                 })
        {
            try { await DeleteCustomAsync(kind, name); } catch { /* best-effort */ }
        }

        try { await _client.DeleteAsync<V1Pod>(_seedPod, Ns, CancellationToken.None); } catch { /* already gone */ }
        try { await _client.DeleteAsync<V1PersistentVolumeClaim>(_sourcePvc, Ns, CancellationToken.None); } catch { /* best-effort */ }
    }

    [E2EFact(DisplayName = "Import CR copies a PVC snapshot into the target repository (operator-driven)")]
    public async Task PvcSnapshot_ImportCopiesArtifactsAndNginxServesThem()
    {
        var import = new MavenRepositoryImportV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepositoryImport",
            Metadata   = new V1ObjectMeta { Name = _importName, NamespaceProperty = Ns },
            Spec = new MavenRepositoryImportSpec
            {
                TargetRepository = _targetRepo,
                Source           = new ImportSourceSpec
                {
                    PvcSnapshot = new PvcSnapshotSourceSpec { ClaimName = _sourcePvc },
                },
            },
        };

        await _client.CreateAsync(import, CancellationToken.None);

        // Wait for the operator to drive the import to Succeeded.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        MavenRepositoryImportV1Alpha1? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await _client.GetAsync<MavenRepositoryImportV1Alpha1>(_importName, Ns, CancellationToken.None);
            if (last is not null && last.Status.Phase is ImportPhase.Succeeded or ImportPhase.Failed or ImportPhase.PartiallyFailed)
                break;
            await Task.Delay(5_000);
        }

        Assert.NotNull(last);
        Assert.True(last!.Status.Phase == ImportPhase.Succeeded,
            $"Import ended in {last.Status.Phase}: " +
            string.Join("; ", last.Status.Conditions.Select(c => $"{c.Type}={c.Reason}")));
        Assert.Equal(2L, last.Status.ArtifactsCopied);

        // Both artifacts must now be served by the target repository's NGINX.
        await PortForwardTargetAsync();
        try
        {
            var jar = await _http!.GetByteArrayAsync($"{RepoUrl()}/{JarPath}");
            Assert.Equal(JarBytes, jar);

            var pom = await _http!.GetByteArrayAsync($"{RepoUrl()}/{PomPath}");
            Assert.Equal(PomBytes, pom);
        }
        finally
        {
            _portForward?.Kill(entireProcessTree: true);
            _portForward?.Dispose();
            _portForward = null;
        }
    }

    // ── Setup helpers ───────────────────────────────────────────────────────────

    private string RepoUrl() => $"http://localhost:{_localPort}/repository/{_targetRepo}";

    private async Task CreateSourcePvcAsync(string name, string size)
    {
        await _client.CreateAsync(new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind       = "PersistentVolumeClaim",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = Ns },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes  = new List<string> { "ReadWriteOnce" },
                Resources    = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) },
                },
            },
        }, CancellationToken.None);

        // Do NOT wait for bound here: the cluster's default StorageClass binds
        // with WaitForFirstConsumer — the PVC stays Pending until a Pod mounts
        // it (the seeder pod below is the first consumer).
    }

    private async Task SeedSourcePvcAsync()
    {
        var pod = new V1Pod
        {
            ApiVersion = "v1",
            Kind       = "Pod",
            Metadata   = new V1ObjectMeta { Name = _seedPod, NamespaceProperty = Ns },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                Containers    = [new V1Container
                {
                    Name  = "seeder",
                    Image = "nginx:1.27-alpine",
                    Command = ["sh", "-c"],
                    Args = new List<string>
                    {
                        // nginx image has busybox sh; write both files with printf.
                        $"mkdir -p /data/$(dirname {JarPath}) && " +
                        // Escape() returns the fully-quoted shell literal — do NOT add another
                        // pair of quotes: ''literal'' makes busybox ash strip all spaces.
                        // printf %s writes exact bytes (heredocs would append a trailing newline).
                        $"printf %s {Escape(JarBytes)} > /data/{JarPath} && " +
                        $"printf %s {Escape(PomBytes)} > /data/{PomPath}",
                    },
                    VolumeMounts = new List<V1VolumeMount> { new() { Name = "pvc", MountPath = "/data" } },
                }],
                Volumes = [new V1Volume
                {
                    Name          = "pvc",
                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = _sourcePvc },
                }],
            },
        };

        await _client.CreateAsync(pod, CancellationToken.None);

        // The seeder pod is the PVC's first consumer; its scheduling triggers binding.
        await WaitForPvcBoundAsync(name: _sourcePvc);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var p  = await _client.GetAsync<V1Pod>(_seedPod, Ns, CancellationToken.None);
            if (p is not null && p.Status?.Phase == "Succeeded") return;
            if (p is not null && p.Status?.Phase == "Failed")
                throw new InvalidOperationException($"Seeder pod failed: {GetPodLogs()}");
            await Task.Delay(2_000);
        }

        var logs = await GetPodLogs();
        throw new TimeoutException($"Seeder pod did not finish in 5 min. Logs:\n{logs}");
    }

    private async Task<string> GetPodLogs()
    {
        try
        {
            using var sr = new StreamReader(await _raw.CoreV1.ReadNamespacedPodLogAsync(Ns, _seedPod, "seeder", cancellationToken: CancellationToken.None));
            return await sr.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            return $"(no logs: {ex.Message})";
        }
    }

    private async Task CreateTargetRepositoryAsync()
    {
        await _client.CreateAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = _targetRepo, NamespaceProperty = Ns },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec
                {
                    Size           = "1Gi",
                    AccessMode     = "ReadWriteOnce",
                    DeletionPolicy = DeletionPolicy.Delete,
                },
                Metrics = new MetricsSpec { Enabled = false },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        }, CancellationToken.None);
    }

    private async Task WaitForTargetReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var repo  = await _client.GetAsync<MavenRepositoryV1Alpha1>(_targetRepo, Ns, CancellationToken.None);
            if (repo is not null && repo.Status.Phase == RepositoryPhase.Ready) return;
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"Target repository {_targetRepo} did not reach Ready within 5 min");
    }

    private async Task PortForwardTargetAsync()
    {
        _localPort   = GetFreePort();
        var psi      = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{_targetRepo}-svc {_localPort}:80 -n {Ns}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        _portForward = new Process { StartInfo = psi };
        _portForward.Start();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, _localPort);
                break;
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_localPort}") };
    }

    /// <summary>
    /// Best-effort removal of leftovers from previously failed runs. The namespace is
    /// shared across runs and xUnit skips DisposeAsync when InitializeAsync throws,
    /// so stale imp-* resources would otherwise accumulate (and pin RWO PVCs on the single node).
    /// </summary>
    private async Task CleanupStaleArtifactsAsync()
    {
        foreach (var repo in await _client.ListAsync<MavenRepositoryV1Alpha1>(Ns, cancellationToken: CancellationToken.None))
            if ((repo.Metadata?.Name ?? "").StartsWith("imp-"))
                try { await _client.DeleteAsync(repo, CancellationToken.None); } catch { /* best-effort */ }

        foreach (var import in await _client.ListAsync<MavenRepositoryImportV1Alpha1>(Ns, cancellationToken: CancellationToken.None))
            if ((import.Metadata?.Name ?? "").StartsWith("imp-"))
                try { await _client.DeleteAsync(import, CancellationToken.None); } catch { /* best-effort */ }

        foreach (var pod in await _client.ListAsync<V1Pod>(Ns, cancellationToken: CancellationToken.None))
            if ((pod.Metadata?.Name ?? "").StartsWith("imp-seed-"))
                try { await _client.DeleteAsync(pod, CancellationToken.None); } catch { /* best-effort */ }

        foreach (var pvc in await _client.ListAsync<V1PersistentVolumeClaim>(Ns, cancellationToken: CancellationToken.None))
            if ((pvc.Metadata?.Name ?? "").StartsWith("imp-src-"))
                try { await _client.DeleteAsync(pvc, CancellationToken.None); } catch { /* best-effort */ }
    }

    private async Task EnsureNamespaceAsync(string name)
    {
        try
        {
            await _client.CreateAsync(new V1Namespace
            {
                ApiVersion = "v1",
                Kind       = "Namespace",
                Metadata   = new V1ObjectMeta { Name = name },
            }, CancellationToken.None);
        }
        catch { /* already exists */ }
    }

    private async Task WaitForPvcBoundAsync(string name)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var pvc = await _client.GetAsync<V1PersistentVolumeClaim>(name, Ns, CancellationToken.None);
            if (pvc is not null && string.Equals(pvc.Status?.Phase, "Bound", StringComparison.OrdinalIgnoreCase))
                return;
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"PVC {name} did not bind within 2 min");
    }

    private async Task DeleteCustomAsync(Type crdType, string name)
    {
        if (crdType == typeof(MavenRepositoryImportV1Alpha1))
            await _client.DeleteAsync<MavenRepositoryImportV1Alpha1>(name, Ns, CancellationToken.None);
        else
            await _client.DeleteAsync<MavenRepositoryV1Alpha1>(name, Ns, CancellationToken.None);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Single-quote escaping for sh -c inline scripts.</summary>
    private static string Escape(byte[] bytes) =>
        "'" + Encoding.UTF8.GetString(bytes).Replace("'", "'\\''") + "'";
}
