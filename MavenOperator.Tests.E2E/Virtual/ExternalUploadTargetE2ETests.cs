using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Tests.E2E.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace MavenOperator.Tests.E2E.Virtual;

/// <summary>
/// E2E coverage for virtual-repo upload fan-out to an EXTERNAL hosted repository:
/// the artifact is PUT once against the Virtual repo and must land in BOTH a
/// local Hosted member (via nginx DAV) AND a remote Reposilite instance reached
/// through a Proxy-type member's <c>spec.upstream.url</c> (direct fan-out from
/// the C# proxy, bypassing our own nginx).
///
/// In-cluster topology:
///   client --PUT--> {virt}-svc (nginx) --> {virt}-proxy-svc (C# fan-out)
///         ├──> {prod}-svc  (Hosted CR, member #1)
///         └──> http://{rs}.<ns>.svc.cluster.local:8082/releases-ext
///              (Reposilite pod, Proxy CR member #2's upstream url)
/// </summary>
[Collection(ExternalUploadTargetE2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class ExternalUploadTargetE2ETests : IDisposable
{
    private readonly ExternalUploadTargetE2EFixture _fx;

    public ExternalUploadTargetE2ETests(ExternalUploadTargetE2EFixture fx) => _fx = fx;

    [E2EFact]
    public async Task Upload_FansOut_To_HostedMember_And_Reposilite_ExternalTarget()
    {
        const string jarPath  = "com/example/widget/1.0.0/widget-1.0.0.jar";
        const string pomPath  = "com/example/widget/1.0.0/widget-1.0.0.pom";

        var jarBytes = System.Text.Encoding.UTF8.GetBytes($"PK-fake-widget-jar-{_fx.Suffix}");
        var pomXml   = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>widget</artifactId>
              <version>1.0.0</version>
            </project>
            """;
        var pomBytes = System.Text.Encoding.UTF8.GetBytes(pomXml);

        // 1. Upload once against the Virtual repository → must fan out to both targets.
        foreach (var (path, bytes) in new[] { (jarPath, jarBytes), (pomPath, pomBytes) })
        {
            var resp = await _fx.UploadToVirtualAsync(path, bytes);
            Assert.True(resp.StatusCode is >= HttpStatusCode.OK and <= HttpStatusCode.MultiStatus,
                $"PUT /repository/{_fx.VirtName}/{path} → {(int)resp.StatusCode} {Truncate(await SafeReadAsync(resp), 200)}");
            resp.Dispose();
        }

        // 2. Artifact must be retrievable from the Hosted member (nginx DAV on PVC).
        foreach (var (path, bytes) in new[] { (jarPath, jarBytes), (pomPath, pomBytes) })
        {
            var (status, body) = await _fx.DownloadFromHostedAsync(path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(bytes, body ?? Array.Empty<byte>());
        }

        // 3. Artifact must be retrievable from the REMOTE Reposilite instance —
        //    proof that fan-out reached spec.upstream.url with correct credentials.
        foreach (var (path, bytes) in new[] { (jarPath, jarBytes), (pomPath, pomBytes) })
        {
            var (status, body) = await _fx.DownloadFromReposiliteAsync(path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(bytes, body ?? Array.Empty<byte>());
        }
    }

    public void Dispose() => _fx.DisposeLeases();

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return "<unreadable>"; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

[CollectionDefinition(ExternalUploadTargetE2ECollection.CollectionName)]
public sealed class ExternalUploadTargetE2ECollection : ICollectionFixture<ExternalUploadTargetE2EFixture>
{
    public const string CollectionName = "ExternalUploadTargetE2E";
}

/// <summary>
/// Provisioning fixture: Hosted CR (releases-prod), Proxy CR whose upstream is an
/// in-cluster Reposilite pod, and a Virtual CR with upload fan-out to both.
/// Self-heals leaked resources from crashed prior runs at start of InitializeAsync
/// (xUnit v2 skips DisposeAsync when InitializeAsync throws).
/// </summary>
public sealed class ExternalUploadTargetE2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";

    // Reposilite hosted repository id — must match the last path segment of the
    // Proxy CR's spec.upstream.url.
    public const string ReleasesExtId = "releases-ext";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];
    public string Suffix      => _suffix;
    public string ProdName    => $"ext-up-prod-{_suffix}";
    public string ProxyName   => $"ext-up-ext-{_suffix}";
    public string VirtName    => $"ext-up-virt-{_suffix}";
    public string RsAppName   => $"rsil-{_suffix}";

    // Reposilite credentials (bcrypt hash embedded — deterministic, no external deps).
    // Reposilite token created at boot via `-t <name>:<secret>` (full permissions).
    private const string RsUser     = "e2e-uploader";
    private const string RsPassword = "rsil-s3cret";

    // Hosted target nginx credentials (fan-out auth for member #1).
    private const string NginxUser     = "deployer";
    private const string NginxPassword = "s3cr3t";

    // Client credentials for uploading TO the virtual repo.
    public const string ClientUploadUser     = "client-deployer";
    public const string ClientUploadPassword = "client-s3cr3t";

    private IKubernetesClient _client = null!;
    private Kubernetes _raw = null!;
    private HttpClient? _virtualHttp;
    private readonly List<Process?> _pfProcesses = [];

    private static string RsImage =>
        Environment.GetEnvironmentVariable("REPOSILITE_IMAGE")
            ?? "ghcr.io/dzikoysk/reposilite:3.2.4";

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        _client = new KubernetesClient(config);
        _raw    = new Kubernetes(config);

        await CleanupStaleRunsAsync(); // self-heal leaked resources (xUnit skips Dispose on init failure)

        try
        {
            await EnsureNamespaceAsync(OperatorNamespace);

            // Secrets: hosted nginx auth, Reposilite upstream auth (shared by the Proxy CR
            // and as per-target fan-out credentials), shared + client virtual upload creds.
            await EnsureCredentialSecretAsync($"{ProdName}-upload", NginxUser, NginxPassword);
            var rsUpstreamSecret = $"{ProxyName}-upstream";
            await EnsureCredentialSecretAsync(rsUpstreamSecret, RsUser, RsPassword);
            await EnsureCredentialSecretAsync($"{VirtName}-shared-upload", NginxUser, NginxPassword);
            await EnsureCredentialSecretAsync($"{VirtName}-client-upload", ClientUploadUser, ClientUploadPassword);

            // ── In-cluster Reposilite (the "external" upstream) ────────────────
            var rsConfCm = $"{RsAppName}-conf";
            await CreateReposiliteConfigMapAsync(rsConfCm);
            await CreateReposiliteDeploymentAsync(RsImage, rsConfCm);
            await CreateServiceAsync(RsAppName, 8082, 8082);
            await WaitForPodReadyAsync(labelSelector: $"app={RsAppName}", timeoutSeconds: 600);
            // Kubelet "Ready" fires before the JVM binds 8082 — wait for HTTP.
            await WaitUntilReposiliteRespondsAsync();

            // ── Hosted member #1 ───────────────────────────────────────────────
            await CreateHostedRepositoryAsync(ProdName, $"{ProdName}-upload");
            await PodReadinessHelper.WaitForNginxReadyAsync(_client, OperatorNamespace, ProdName);

            // ── Proxy member #2 → upstream = Reposilite cluster DNS url ────────
            // Reposilite 3.x Maven URL scheme is http://host:<port>/<repo-id> (no /repository/ prefix).
            var rsUpstreamUrl = $"http://{RsAppName}.{OperatorNamespace}.svc.cluster.local:8082/{ReleasesExtId}";
            await CreateProxyRepositoryAsync(ProxyName, rsUpstreamUrl, rsUpstreamSecret);
            await PodReadinessHelper.WaitForNginxReadyAsync(_client, OperatorNamespace, ProxyName);

            // ── Virtual repo with fan-out to BOTH targets ──────────────────────
            var virtUploadSecret = $"{VirtName}-shared-upload";
            await CreateVirtualRepositoryAsync(VirtName, [ProdName, ProxyName], rsUpstreamSecret, virtUploadSecret);
            await PodReadinessHelper.WaitForNginxReadyAsync(_client, OperatorNamespace, VirtName);
            await WaitForProxySvcReadyAsync(VirtName);

            // Port-forward the virtual nginx svc for client-side assertions.
            var (port, _) = await PortForwardServiceAsync($"{VirtName}-svc", 80);
            _virtualHttp = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(30) };
        }
        catch
        {
            DisposeLeases(); // don't leak port-forwards / partial resources on init failure
            throw;
        }
    }

    public Task DisposeAsync()
    {
        DisposeLeases();
        return Task.CompletedTask;
    }

    /// <summary>Kills any lingering kubectl port-forward processes (safe to call repeatedly).</summary>
    public void DisposeLeases()
    {
        _virtualHttp?.Dispose();
        foreach (var pf in _pfProcesses)
        {
            try { if (pf is not null && !pf.HasExited) pf.Kill(entireProcessTree: true); } catch { }
            pf?.Dispose();
        }
        _pfProcesses.Clear();
    }

    // ── Test-facing helpers ───────────────────────────────────────────────────

    public async Task<HttpResponseMessage> UploadToVirtualAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var cred = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{ClientUploadUser}:{ClientUploadPassword}"));
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/repository/{VirtName}/{path}")
        {
            Content = new ByteArrayContent(content),
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", cred) },
        };
        return await _virtualHttp!.SendAsync(req, ct);
    }

    public async Task<(HttpStatusCode Status, byte[]? Body)> DownloadFromHostedAsync(string path, CancellationToken ct = default)
    {
        var (port, pf) = await PortForwardServiceAsync($"{ProdName}-svc", 80);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            using var resp = await client.GetAsync($"/repository/{ProdName}/{path}", ct);
            return (resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null);
        }
        finally { KillPf(pf); }
    }

    public async Task<(HttpStatusCode Status, byte[]? Body)> DownloadFromReposiliteAsync(string path, CancellationToken ct = default)
    {
        var (port, pf) = await PortForwardServiceAsync(RsAppName, 8082);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            var cred = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{RsUser}:{RsPassword}"));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{ReleasesExtId}/{path}")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", cred) },
            };
            using var resp = await client.SendAsync(req, ct);
            return (resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null);
        }
        finally { KillPf(pf); }
    }

    // ── Provisioning internals ────────────────────────────────────────────────

    /// <summary>Reposilite 3.x shared configuration (JSON). Users are NOT configurable here — the
    /// uploader token is created at boot with `-t name:secret` (see RsOpts).</summary>
    private static string SharedConfigJson => $$"""
        {
          "maven" : {
            "repositories" : [ {
              "id" : "{{ReleasesExtId}}",
              "visibility" : "PUBLIC",
              "storageProvider" : { "type" : "fs", "quota" : "100%", "mount" : "" },
              "redeployment" : false,
              "preserveSnapshots" : false,
              "proxied" : [ ]
            } ]
          }
        }
        """;

    /// <summary>
    /// entrypoint.sh passes $REPOSILITE_OPTS straight to java; it appends a default working
    /// dir only when no -wd is present. The shared config file location must be given via -sc.
    /// </summary>
    private static string RsOpts =>
        $"-wd /data -p 8082 -sc /data/conf/configuration.shared.json -t {RsUser}:{RsPassword}";

    private async Task CreateReposiliteConfigMapAsync(string name)
    {
        await EnsureObjectAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Data     = new Dictionary<string, string> { ["configuration.shared.json"] = SharedConfigJson },
        });
    }

    private async Task CreateReposiliteDeploymentAsync(string image, string confCmName)
    {
        await EnsureObjectAsync(new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind       = "Deployment",
            Metadata   = new V1ObjectMeta { Name = RsAppName, NamespaceProperty = OperatorNamespace },
            Spec = new V1DeploymentSpec
            {
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = RsAppName } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = RsAppName } },
                    Spec     = new V1PodSpec
                    {
                        Containers =
                        [
                            new V1Container
                            {
                                Name    = "reposilite",
                                Image   = image,
                                Ports   = [new V1ContainerPort { ContainerPort = 8082 }],
                                Env     = [new V1EnvVar { Name = "REPOSILITE_OPTS", Value = RsOpts }],
                                ReadinessProbe = new V1Probe
                                {
                                    // "/" (dashboard) is the only unauthenticated 2xx path.
                                    HttpGet    = new V1HTTPGetAction { Path = "/", Port = 8082 },
                                    // JVM takes up to a couple of minutes on first boot; the HTTP
                                    // poll in InitializeAsync is the authoritative gate.
                                    InitialDelaySeconds = 5,
                                    PeriodSeconds       = 10,
                                    FailureThreshold    = 60,
                                },
                                VolumeMounts =
                                [
                                    new V1VolumeMount { Name = "data", MountPath = "/data" },
                                    new V1VolumeMount { Name = "conf", MountPath = "/data/conf" },
                                ],
                            },
                        ],
                        Volumes =
                        [
                            new V1Volume { Name = "data", EmptyDir = new V1EmptyDirVolumeSource() },
                            new V1Volume { Name = "conf", ConfigMap = new V1ConfigMapVolumeSource { Name = confCmName } },
                        ],
                    },
                },
            },
        });

    }

    private async Task CreateServiceAsync(string name, int port, int targetPort)
    {
        await EnsureObjectAsync(new V1Service
        {
            ApiVersion = "v1",
            Kind       = "Service",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Spec = new V1ServiceSpec
            {
                Selector  = new Dictionary<string, string> { ["app"] = name },
                Ports     = [new V1ServicePort { Port = port, TargetPort = targetPort }],
            },
        });
    }

    private async Task CreateHostedRepositoryAsync(string name, string uploadSecretRef)
    {
        await EnsureObjectAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", AccessMode = "ReadWriteOnce", DeletionPolicy = DeletionPolicy.Delete },
                Metrics = new MetricsSpec { Enabled = false },
                Auth   = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec
                    {
                        Policy  = AuthPolicy.Authenticated,
                        Users   = [new UserRef { SecretRef = uploadSecretRef, Role = UserRole.Deployer }],
                    },
                },
            },
        });
    }

    private async Task CreateProxyRepositoryAsync(string name, string upstreamUrl, string upstreamSecret)
    {
        await EnsureObjectAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Proxy,
                Metrics = new MetricsSpec { Enabled = false },
                Upstream = new UpstreamSpec
                {
                    Url  = upstreamUrl,
                    Auth = new UpstreamAuthSpec { SecretRef = upstreamSecret },
                    Upload = new ProxyUploadSpec
                    {
                        Enabled               = true,
                        Mode                  = ProxyUploadMode.Passthrough,
                        UpstreamCredentialsRef = new LocalObjectReference { Name = upstreamSecret },
                    },
                },
            },
        });
    }

    private async Task CreateVirtualRepositoryAsync(string name, List<string> members, string proxyUpstreamSecret, string sharedSecret)
    {
        await EnsureObjectAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Virtual,
                Virtual = new VirtualSpec
                {
                    Members                 = members,
                    MetadataCacheTtlSeconds = 60,
                    Upload = new VirtualUploadSpec
                    {
                        // Member #1 (Hosted): shared nginx creds.
                        // Member #2 (Proxy→Reposilite): per-target upstream creds — the fan-out
                        // goes directly to spec.upstream.url, so credentials differ from member #1.
                        Targets =
                        [
                            new VirtualUploadTarget { Name = members[0] },
                            new VirtualUploadTarget { Name = members[1], CredentialsRef = new LocalObjectReference { Name = proxyUpstreamSecret } },
                        ],
                        SharedCredentialsRef = new LocalObjectReference { Name = sharedSecret },
                    },
                },
                Metrics = new MetricsSpec { Enabled = false },
                Auth   = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec
                    {
                        Policy  = AuthPolicy.Authenticated,
                        Users   = [new UserRef { SecretRef = $"{name}-client-upload", Role = UserRole.Deployer }],
                    },
                },
            },
        });
    }

    private async Task EnsureCredentialSecretAsync(string name, string username, string password)
    {
        await EnsureObjectAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Type     = "Opaque",
            Data     = new Dictionary<string, byte[]>
            {
                ["username"] = System.Text.Encoding.UTF8.GetBytes(username),
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password),
            },
        });
    }

    private async Task EnsureNamespaceAsync(string name)
    {
        try
        {
            await _client.CreateAsync<V1Namespace>(new V1Namespace { Metadata = new V1ObjectMeta { Name = name } }, CancellationToken.None);
        }
        catch { /* already exists */ }
    }

    private async Task EnsureObjectAsync<T>(T body) where T : IKubernetesObject<V1ObjectMeta>
    {
        try { await _client.CreateAsync(body, CancellationToken.None); }
        catch { /* AlreadyExists from a crashed prior run — proceed */ }
    }

    private async Task WaitForPodReadyAsync(string labelSelector, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var pods = await _client.ListAsync<V1Pod>(OperatorNamespace,
                labelSelector: labelSelector, cancellationToken: CancellationToken.None);
            if (pods.Any(p => p.Status?.Phase == "Running"
                              && p.Status.ContainerStatuses is { Count: > 0 } cs && cs.All(c => c.Ready)))
                return;
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"Pod(s) '{labelSelector}' in '{OperatorNamespace}' not Ready within {timeoutSeconds}s.");
    }

    private async Task WaitForProxySvcReadyAsync(string repoName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await _client.GetAsync<V1Service>($"{repoName}-proxy-svc", OperatorNamespace, CancellationToken.None) is not null)
                    break;
            }
            catch { /* wait for reconcile */ }
            await Task.Delay(1_000);
        }

        var pods = await _client.ListAsync<V1Pod>(OperatorNamespace,
            labelSelector: $"app={repoName}-proxy", cancellationToken: CancellationToken.None);
        if (pods.Any(p => p.Status?.Phase == "Running"
                          && p.Status.ContainerStatuses is { Count: > 0 } cs && cs.All(c => c.Ready)))
            return;

        // Fall back to a short grace poll — reconcile may not have created the pod yet.
        var podDeadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < podDeadline)
        {
            pods = await _client.ListAsync<V1Pod>(OperatorNamespace,
                labelSelector: $"app={repoName}-proxy", cancellationToken: CancellationToken.None);
            if (pods.Any(p => p.Status?.Phase == "Running"
                             && p.Status.ContainerStatuses is { Count: > 0 } cs && cs.All(c => c.Ready)))
                return;
            await Task.Delay(1_000);
        }
        throw new TimeoutException($"Virtual proxy pod for '{repoName}' did not become Ready.");
    }

    /// <summary>
    /// The kubelet reports the container Ready as soon as it starts — well before the JVM
    /// binds 8082. Poll a port-forwarded HTTP probe until any response arrives (401/404 count).
    /// </summary>
    private async Task WaitUntilReposiliteRespondsAsync()
    {
        var (port, pf) = await PortForwardServiceAsync(RsAppName, 8082);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var resp = await client.GetAsync($"/repository/{ReleasesExtId}/", CancellationToken.None);
                    return; // any HTTP response means the server is up
                }
                catch
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Reposilite did not start listening on 8082 in time.");
                    await Task.Delay(2_000);
                }
            }
        }
        finally { KillPf(pf); }
    }

    private async Task<(int Port, Process Process)> PortForwardServiceAsync(string svcName, int remotePort)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{svcName} {localPort}:{remotePort} -n {OperatorNamespace}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        var process = new Process { StartInfo = psi };
        process.Start();
        _pfProcesses.Add(process);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, localPort);
                return (localPort, process);
            }
            catch { await Task.Delay(500); }
        }
        throw new TimeoutException($"port-forward to svc/{svcName} did not come up.");
    }

    private static void KillPf(Process? pf)
    {
        try { if (pf is not null && !pf.HasExited) pf.Kill(entireProcessTree: true); } catch { }
        pf?.Dispose();
    }

    /// <summary>Best-effort removal of resources leaked by crashed prior runs.</summary>
    private async Task CleanupStaleRunsAsync()
    {
        var ct = CancellationToken.None;

        // Stale MavenRepository CRs from earlier runs (random suffixes — match on prefix).
        var repos = await _client.ListAsync<MavenRepositoryV1Alpha1>(OperatorNamespace, cancellationToken: ct);
        foreach (var repo in repos.Where(r => r.Metadata.Name is { } n && n.StartsWith("ext-up-")))
            try { await _client.DeleteAsync(repo, ct); } catch { /* best-effort */ }

        // Stale Reposilite deployments/services/configmaps.
        foreach (var d in (await _raw.ListDeploymentForAllNamespacesAsync(cancellationToken: ct)).Items)
            if (d.Metadata.Name is { } n && n.StartsWith("rsil-"))
                try { await _raw.DeleteNamespacedDeploymentAsync(n, d.Metadata.NamespaceProperty ?? OperatorNamespace, cancellationToken: ct); } catch { }
        foreach (var s in (await _raw.ListServiceForAllNamespacesAsync(cancellationToken: ct)).Items)
            if (s.Metadata.Name is { } n && n.StartsWith("rsil-"))
                try { await _raw.DeleteNamespacedServiceAsync(n, s.Metadata.NamespaceProperty ?? OperatorNamespace, cancellationToken: ct); } catch { }
        foreach (var c in (await _raw.ListConfigMapForAllNamespacesAsync(cancellationToken: ct)).Items)
            if (c.Metadata.Name is { } n && n.StartsWith("rsil-"))
                try { await _raw.DeleteNamespacedConfigMapAsync(n, c.Metadata.NamespaceProperty ?? OperatorNamespace, cancellationToken: ct); } catch { }
    }
}
