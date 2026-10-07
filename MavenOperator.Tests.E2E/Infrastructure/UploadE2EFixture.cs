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
/// xUnit fixture for upload E2E tests (Phase 6).
///
/// Creates:
///   - Two Hosted repositories (upload targets)
///   - One Virtual repository with upload fan-out enabled pointing to both members
///   - Credential secrets for upload authentication
///
/// Port-forwards the Virtual NGINX service so test assertions can use plain HTTP.
/// </summary>
public sealed class UploadE2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";

    // Public repo names / URLs surfaced to tests
    public string Target1Name   { get; private set; } = string.Empty;
    public string Target2Name   { get; private set; } = string.Empty;
    public string VirtualName   { get; private set; } = string.Empty;
    public HttpClient HttpClient { get; private set; } = null!;
    public IKubernetesClient Client { get; private set; } = null!;

    // Credentials for upload fan-out (shared credentials used by Virtual to authenticate with targets)
    public string UploadUser     { get; } = "deployer";
    public string UploadPassword { get; } = "s3cr3t";

    // Client credentials for uploading TO the virtual repo
    public string ClientUploadUser     { get; } = "client-deployer";
    public string ClientUploadPassword { get; } = "client-s3cr3t";

    private Process? _portForwardProcess;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client = new KubernetesClient(config);

        var suffix   = Guid.NewGuid().ToString("N")[..6];
        Target1Name  = $"e2e-up-t1-{suffix}";
        Target2Name  = $"e2e-up-t2-{suffix}";
        VirtualName  = $"e2e-up-virt-{suffix}";

        // Ensure the E2E namespace exists
        try
        {
            await Client.CreateAsync<V1Namespace>(
                new V1Namespace { Metadata = new V1ObjectMeta { Name = OperatorNamespace } },
                CancellationToken.None);
        }
        catch { /* already exists */ }

        // Create upload-credential Secrets for each target (used by targets to authenticate uploads)
        foreach (var name in new[] { Target1Name, Target2Name })
        {
            await EnsureCredentialSecretAsync($"{name}-upload", UploadUser, UploadPassword);
        }

        // Shared credentials secret: Virtual uses this to upload TO its targets
        await EnsureCredentialSecretAsync($"{VirtualName}-shared-upload", UploadUser, UploadPassword);

        // Client credential secret: client uses this to upload TO the virtual repo
        await EnsureCredentialSecretAsync($"{VirtualName}-client-upload", ClientUploadUser, ClientUploadPassword);

        // Create Hosted target 1
        await CreateHostedAsync(Target1Name, $"{Target1Name}-upload");
        // Create Hosted target 2
        await CreateHostedAsync(Target2Name, $"{Target2Name}-upload");

        // Wait for both target NGINX pods to be ready before creating the Virtual
        await WaitForNginxReadyAsync(Target1Name);
        await WaitForNginxReadyAsync(Target2Name);

        // Create the Virtual repository with upload fan-out enabled
        await CreateVirtualWithUploadsAsync(VirtualName, [Target1Name, Target2Name]);

        // Wait for the Virtual NGINX to be ready
        await WaitForNginxReadyAsync(VirtualName);
        await WaitForProxyReadyAsync(VirtualName);

        var baseUrl  = await ResolveBaseUrlAsync($"{VirtualName}-svc");
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

        // Delete in reverse order: virtual first, then targets
        foreach (var name in new[] { VirtualName, Target1Name, Target2Name })
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

    /// <summary>Uploads an artifact to the Virtual repo via HTTP PUT (triggers fan-out).</summary>
    public async Task<HttpResponseMessage> UploadToVirtualAsync(
        string path, byte[] content, CancellationToken ct = default)
    {
        var cred   = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes($"{ClientUploadUser}:{ClientUploadPassword}"));

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/repository/{VirtualName}/{path}");
        req.Content = new ByteArrayContent(content);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", cred);

        return await HttpClient.SendAsync(req, ct);
    }

    /// <summary>Downloads an artifact from a specific target via port-forward.</summary>
    public async Task<(HttpStatusCode Status, byte[]? Body)> DownloadFromTargetAsync(
        string targetName, string path, CancellationToken ct = default)
    {
        var (localPort, process) = await PortForwardServiceAsync($"{targetName}-svc");
        try
        {
            var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{localPort}") };
            using var resp = await client.GetAsync($"/repository/{targetName}/{path}", ct);
            var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
            return (resp.StatusCode, body);
        }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
        }
    }

    /// <summary>Downloads an artifact from the Virtual repo.</summary>
    public async Task<(HttpStatusCode Status, byte[]? Body)> DownloadFromVirtualAsync(
        string path, CancellationToken ct = default)
    {
        using var resp = await HttpClient.GetAsync($"/repository/{VirtualName}/{path}", ct);
        var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
        return (resp.StatusCode, body);
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

    private async Task CreateVirtualWithUploadsAsync(string name, List<string> members)
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
                    Upload = new VirtualUploadSpec
                    {
                        // Fan out to all members using shared credentials.
                        Targets = members.Select(m => new VirtualUploadTarget { Name = m }).ToList(),
                        SharedCredentialsRef = new LocalObjectReference { Name = $"{name}-shared-upload" },
                    },
                },
                Metrics = new MetricsSpec { Enabled = false },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec
                    {
                        Policy     = AuthPolicy.Authenticated,
                        Users =
                        [
                            new UserRef { SecretRef = $"{name}-client-upload", Role = UserRole.Deployer },
                        ],
                    },
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

    private async Task WaitForNginxReadyAsync(string repoName)
    {
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
            catch { /* wait for reconcile */ }

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

[CollectionDefinition(UploadE2ECollection.CollectionName)]
public sealed class UploadE2ECollection : ICollectionFixture<UploadE2EFixture>
{
    public const string CollectionName = "UploadE2E";
}
