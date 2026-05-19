using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// xUnit fixture for Virtual repository E2E tests.
///
/// Creates TWO Hosted repositories (the "members"), waits for them to be ready,
/// then creates a Virtual repository that fans out to both. Port-forwards the
/// Virtual NGINX service to a local port so test assertions can use plain HTTP.
///
/// Teardown deletes all three MavenRepository CRDs; DeletionPolicy=Delete ensures
/// storage is also removed.
/// </summary>
public sealed class VirtualE2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";

    // Public repo names / URLs surfaced to tests
    public string Member1Name    { get; private set; } = string.Empty;
    public string Member2Name    { get; private set; } = string.Empty;
    public string VirtualName    { get; private set; } = string.Empty;
    public string VirtualUrl     { get; private set; } = string.Empty;
    public HttpClient HttpClient { get; private set; } = null!;
    public IKubernetesClient Client { get; private set; } = null!;

    // Credentials for member upload (anonymous download for simplicity in tests)
    public string UploadUser     { get; } = "deployer";
    public string UploadPassword { get; } = "s3cr3t";

    private Process? _portForwardProcess;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client = new KubernetesClient(config);

        var suffix   = Guid.NewGuid().ToString("N")[..6];
        Member1Name = $"e2e-virt-m1-{suffix}";
        Member2Name = $"e2e-virt-m2-{suffix}";
        VirtualName = $"e2e-virt-{suffix}";

        // Ensure the E2E namespace exists
        try
        {
            await Client.CreateAsync<V1Namespace>(
                new V1Namespace { Metadata = new V1ObjectMeta { Name = OperatorNamespace } },
                CancellationToken.None);
        }
        catch { /* already exists */ }

        // Create upload-credential Secrets for each member
        foreach (var memberName in new[] { Member1Name, Member2Name })
        {
            await EnsureCredentialSecretAsync($"{memberName}-upload",
                UploadUser, UploadPassword);
        }

        // Create Hosted member 1
        await CreateHostedAsync(Member1Name, $"{Member1Name}-upload");
        // Create Hosted member 2
        await CreateHostedAsync(Member2Name, $"{Member2Name}-upload");

        // Wait for both member NGINX pods to be ready before creating the Virtual
        await WaitForNginxReadyAsync(Member1Name);
        await WaitForNginxReadyAsync(Member2Name);

        // Create the Virtual repository pointing at both members
        await CreateVirtualAsync(VirtualName, [Member1Name, Member2Name]);

        // Wait for the Virtual NGINX to be ready
        await WaitForNginxReadyAsync(VirtualName);
        // Also wait for the internal C# proxy pod/service. Without this, NGINX can be
        // up while upstream proxy still starts, causing transient/persistent 502s.
        await WaitForProxyReadyAsync(VirtualName);

        var baseUrl  = await ResolveBaseUrlAsync($"{VirtualName}-svc");
        VirtualUrl   = $"{baseUrl.TrimEnd('/')}/repository/{VirtualName}";
        HttpClient   = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(30),
        };
    }

    public async Task DisposeAsync()
    {
        HttpClient?.Dispose();

        if (_portForwardProcess is not null)
        {
            try { _portForwardProcess.Kill(entireProcessTree: true); } catch { }
            _portForwardProcess.Dispose();
        }

        // Delete in reverse order: virtual first, then members
        foreach (var name in new[] { VirtualName, Member1Name, Member2Name })
        {
            try
            {
                await Client.DeleteAsync<MavenRepositoryV1Alpha1>(
                    name, OperatorNamespace, CancellationToken.None);
            }
            catch { /* best-effort */ }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Uploads a small artifact to a hosted member repository via HTTP PUT.</summary>
    public async Task UploadArtifactAsync(
        string memberName, string path, byte[] content,
        CancellationToken ct = default)
    {
        var (localPort, _process) = await PortForwardServiceAsync($"{memberName}-svc");
        try
        {
            var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{localPort}") };
            var cred   = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{UploadUser}:{UploadPassword}"));

            using var req = new HttpRequestMessage(HttpMethod.Put,
                $"/repository/{memberName}/{path}");
            req.Content = new ByteArrayContent(content);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", cred);

            var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Created
                && resp.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    $"Upload to {memberName}/{path} failed: {(int)resp.StatusCode}");
            }
        }
        finally
        {
            try { _process?.Kill(entireProcessTree: true); } catch { }
            _process?.Dispose();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task CreateHostedAsync(string name, string uploadSecretRef)
    {
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = OperatorNamespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec
                {
                    Size = "1Gi",
                    AccessMode = "ReadWriteOnce",
                    DeletionPolicy = DeletionPolicy.Delete
                },
                // Disable metrics sidecars in E2E — avoids sidecar image-pull latency.
                Metrics = new MetricsSpec { Enabled = false },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec
                    {
                        Policy     = AuthPolicy.Authenticated,
                        Users =
                        [
                            new UserRef { SecretRef = uploadSecretRef, Role = UserRole.Deployer },
                        ],
                    },
                },
            },
        };

        await Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);
    }

    private async Task CreateVirtualAsync(string name, List<string> members)
    {
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = OperatorNamespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Virtual,
                Virtual = new VirtualSpec
                {
                    Members                 = members,
                    MetadataCacheTtlSeconds = 60,
                },
                // Disable metrics sidecars in E2E — avoids sidecar image-pull latency.
                Metrics = new MetricsSpec { Enabled = false },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        };

        await Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);
    }

    private async Task EnsureCredentialSecretAsync(string name, string username, string password)
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Type     = "Opaque",
            Data     = new Dictionary<string, byte[]>
            {
                ["username"] = System.Text.Encoding.UTF8.GetBytes(username),
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password),
            },
        };

        try { await Client.CreateAsync<V1Secret>(secret, CancellationToken.None); }
        catch { /* already exists from a previous run is fine */ }
    }

    private async Task WaitForNginxReadyAsync(string repoName, int timeoutSeconds = 180)
    {
        // Delegate to the shared helper which uses PodReadyTimeoutSeconds (300 s)
        // and emits diagnostic output on failure. The timeoutSeconds parameter
        // is kept for backward-compat but ignored — the shared constants win.
        await PodReadinessHelper.WaitForNginxReadyAsync(
            Client, OperatorNamespace, repoName);
    }

    private async Task WaitForProxyReadyAsync(string repoName)
    {
        var serviceName = $"{repoName}-proxy-svc";
        var serviceDeadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < serviceDeadline)
        {
            try
            {
                var service = await Client.GetAsync<V1Service>(serviceName, OperatorNamespace, CancellationToken.None);
                if (service is not null)
                    break;
            }
            catch
            {
                // wait for reconcile
            }

            await Task.Delay(1000);
        }

        var labelSelector = $"app={repoName}-proxy";
        var podDeadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < podDeadline)
        {
            var pods = await Client.ListAsync<V1Pod>(OperatorNamespace,
                labelSelector: labelSelector,
                cancellationToken: CancellationToken.None);

            var ready = pods.Any(p =>
                p.Status?.Phase == "Running" &&
                p.Status.ContainerStatuses is { Count: > 0 } statuses &&
                statuses.All(cs => cs.Ready));

            if (ready)
                return;

            await Task.Delay(1000);
        }

        throw new TimeoutException(
            $"Virtual proxy pod for '{repoName}' did not become Ready in namespace '{OperatorNamespace}'.");
    }

    private async Task<string> ResolveBaseUrlAsync(string svcName)
    {
        var explicitBase = Environment.GetEnvironmentVariable("E2E_REPO_BASE_URL");
        if (!string.IsNullOrEmpty(explicitBase))
            return explicitBase.TrimEnd('/');

        var (localPort, process) = await PortForwardServiceAsync(svcName);
        _portForwardProcess = process;
        return $"http://localhost:{localPort}";
    }

    private async Task<(int Port, Process Process)> PortForwardServiceAsync(string svcName)
    {
        var localPort = GetFreePort();
        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{svcName} {localPort}:80 -n {OperatorNamespace}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        var process = new Process { StartInfo = psi };
        process.Start();

        var pfDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < pfDeadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, localPort);
                break;
            }
            catch { await Task.Delay(500); }
        }

        return (localPort, process);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(VirtualE2ECollection.CollectionName)]
public sealed class VirtualE2ECollection : ICollectionFixture<VirtualE2EFixture>
{
    public const string CollectionName = "VirtualE2E";
}

