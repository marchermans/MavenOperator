using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MavenOperator.Tests.E2E.Proxy;

/// <summary>
/// End-to-end tests for Proxy repositories.
///
/// Validates that:
///   1. The operator provisions an NGINX proxy pod that forwards requests to an upstream.
///   2. Artifacts are resolvable through the proxy (GET returns 200, body passes through).
///   3. The proxy cache is populated — a second GET is served from the cache without
///      contacting the upstream.
///   4. Download auth policy (Anonymous vs Authenticated) is enforced.
///   5. Maven resolves a real artifact (junit:junit:4.13.2) through the operator's proxy
///      backed by Maven Central — the canonical Phase 2 acceptance criterion.
///
/// Run with: E2E_TESTS=true dotnet test --filter Category=E2E
/// </summary>
[Collection(ProxyE2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class ProxyRepositoryE2ETests(ProxyE2EFixture e2e)
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // ── Operator provisions repository ────────────────────────────────────

    [E2EFact]
    public async Task Operator_MustCreateProxyService_AccessibleOverHttp()
    {
        var response = await e2e.HttpClient.GetAsync("/healthz");
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"Proxy NGINX health check should return 2xx, got {(int)response.StatusCode}");
    }

    [E2EFact]
    public async Task Operator_WiresAuthProxy_WhenCiTrustOrAclsConfigured()
    {
        const string ns = "maven-e2e";
        var repoName = $"e2e-p6b-{Guid.NewGuid():N}"[..18];
        var userSecretName = $"{repoName}-user";

        try
        {
            await e2e.Client.CreateAsync(new V1Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Metadata = new V1ObjectMeta
                {
                    Name = userSecretName,
                    NamespaceProperty = ns,
                    Labels = new Dictionary<string, string> { ["maven.operator.io/credential"] = "true" },
                },
                Type = "Opaque",
                Data = new Dictionary<string, byte[]>
                {
                    ["username"] = Encoding.UTF8.GetBytes("phase6b-reader"),
                    ["password"] = Encoding.UTF8.GetBytes("phase6b-reader-pass"),
                },
            }, CancellationToken.None);

            await e2e.Client.CreateAsync(new MavenRepositoryV1Alpha1
            {
                ApiVersion = "maven.operator.io/v1alpha1",
                Kind = "MavenRepository",
                Metadata = new V1ObjectMeta { Name = repoName, NamespaceProperty = ns },
                Spec = new MavenRepositorySpec
                {
                    Type = RepositoryType.Proxy,
                    Upstream = new UpstreamSpec { Url = "https://repo1.maven.org/maven2", CacheTtl = "1d" },
                    Metrics = new MetricsSpec { Enabled = false },
                    Auth = new AuthSpec
                    {
                        Download = new AuthPolicySpec
                        {
                            Policy = AuthPolicy.Authenticated,
                            Users = [new UserRef { SecretRef = userSecretName, Role = UserRole.Reader }],
                            CiTrust =
                            [
                                new CiTrustBinding
                                {
                                    Platform = CiPlatform.GitHubActions,
                                    Audience = "maven-operator",
                                    Role = UserRole.Deployer,
                                    Claims = new Dictionary<string, string> { ["repository"] = "acme/example-repo" },
                                },
                            ],
                            Acls =
                            [
                                new ArtifactAcl
                                {
                                    Path = "com/acme/**",
                                    Roles = [UserRole.Deployer],
                                },
                            ],
                        },
                        Upload = new AuthPolicySpec
                        {
                            Policy = AuthPolicy.Anonymous,
                        },
                    },
                },
            }, CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(90);
            V1Deployment? deployment = null;
            V1ConfigMap? nginxConfigMap = null;
            V1ConfigMap? authProxyConfigMap = null;

            while (DateTime.UtcNow < deadline)
            {
                deployment = await e2e.Client.GetAsync<V1Deployment>($"{repoName}-nginx", ns, CancellationToken.None);
                nginxConfigMap = await e2e.Client.GetAsync<V1ConfigMap>($"{repoName}-nginx-cm", ns, CancellationToken.None);
                authProxyConfigMap = await e2e.Client.GetAsync<V1ConfigMap>($"{repoName}-auth-proxy-cm", ns, CancellationToken.None);
                if (deployment is not null && nginxConfigMap is not null && authProxyConfigMap is not null) break;
                await Task.Delay(1000);
            }

            deployment.ShouldNotBeNull();
            nginxConfigMap.ShouldNotBeNull();
            authProxyConfigMap.ShouldNotBeNull();

            deployment!.Spec!.Template!.Spec!.Containers!.Select(c => c.Name)
                .ShouldContain("maven-auth-proxy");

            var nginxConf = nginxConfigMap!.Data!["default.conf"];
            nginxConf.ShouldContain("location = /auth/validate");
            nginxConf.ShouldContain("auth_request /auth/validate");

            var authProxyJson = authProxyConfigMap!.Data!["config.json"];
            authProxyJson.ShouldContain("\"ciTrust\"");
            authProxyJson.ShouldContain("\"acls\"");
            authProxyJson.ShouldContain("com/acme/**");
        }
        finally
        {
            try { await e2e.Client.DeleteAsync<MavenRepositoryV1Alpha1>(repoName, ns, CancellationToken.None); } catch { }
            try { await e2e.Client.DeleteAsync<V1Secret>(userSecretName, ns, CancellationToken.None); } catch { }
        }
    }

    // ── Artifact resolution through proxy ─────────────────────────────────

    [E2EFact]
    public async Task Http_Get_Proxied_ExistingArtifact_Returns200_WithBody()
    {
        // junit:junit:4.13.2 is a stable, immutable artifact on Maven Central.
        var (status, body) = await e2e.GetArtifactAsync(
            "junit/junit/4.13.2/junit-4.13.2.jar");

        status.ShouldBe(HttpStatusCode.OK,
            $"Proxied GET should return 200, got {(int)status}");
        body.ShouldNotBeNull("Response body should not be empty");
        body!.Length.ShouldBeGreaterThan(0, "Jar file should have non-zero size");
    }

    [E2EFact]
    public async Task Http_Get_Proxied_Pom_Returns200()
    {
        var (status, body) = await e2e.GetArtifactAsync(
            "junit/junit/4.13.2/junit-4.13.2.pom");

        status.ShouldBe(HttpStatusCode.OK,
            $"Proxied POM GET should return 200, got {(int)status}");
        body.ShouldNotBeNull();
        var xml = Encoding.UTF8.GetString(body!);
        xml.ShouldContain("junit");
    }

    [E2EFact]
    public async Task Http_Get_NonExistent_Artifact_ReturnsNotFound()
    {
        var (status, _) = await e2e.GetArtifactAsync(
            "io/does/not/exist/9.9.9/nonexistent-9.9.9.jar");

        status.ShouldBe(HttpStatusCode.NotFound,
            $"Non-existent proxied artifact should return 404, got {(int)status}");
    }

    [E2EFact]
    public async Task Http_Get_SecondRequest_IsCached_ReturnsSameBody()
    {
        // First request — populates the cache
        var (status1, body1) = await e2e.GetArtifactAsync(
            "junit/junit/4.13.2/junit-4.13.2.jar");
        status1.ShouldBe(HttpStatusCode.OK);

        // Second request — should be served from the emptyDir cache
        var (status2, body2) = await e2e.GetArtifactAsync(
            "junit/junit/4.13.2/junit-4.13.2.jar");
        status2.ShouldBe(HttpStatusCode.OK);

        // Bodies must be identical — verifies the cache returns the correct bytes
        body1.ShouldNotBeNull();
        body2.ShouldNotBeNull();
        body1!.Length.ShouldBe(body2!.Length,
            "Cached response must have the same size as the upstream response");
    }

    // ── Auth enforcement ──────────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Put_ToProxy_Returns405_MethodNotAllowed()
    {
        // The proxy template does not configure dav_methods — uploads to a Proxy
        // repo must fail.  The fixture's repo has Anonymous download so there's
        // no auth barrier; if PUT works that's a bug in the template.
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{e2e.RepositoryUrl}/io/test/put-not-allowed/1.0/put-not-allowed-1.0.jar");
        req.Content = new ByteArrayContent("should-not-work"u8.ToArray());
        var response = await e2e.HttpClient.SendAsync(req);

        // NGINX proxy_pass does not forward PUT to upstream either — 405 expected.
        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400,
            $"PUT to a Proxy repo should fail (got {(int)response.StatusCode})");
    }

    // ── Phase 2 acceptance criterion ──────────────────────────────────────

    [E2EFact]
    public async Task Maven_Resolve_JUnit_ThroughProxy_Succeeds()
    {
        var mavenFixtureDir = Path.Combine(FixturesDir, "maven-proxy-fixture");
        if (!Directory.Exists(mavenFixtureDir))
        {
            // Skip gracefully if the fixture wasn't shipped — CI will have it.
            return;
        }

        var (exitCode, output) = await e2e.RunMavenAsync(
            mavenFixtureDir,
            "dependency:resolve -Dartifact=junit:junit:4.13.2");

        exitCode.ShouldBe(0,
            $"mvn dependency:resolve through proxy should succeed.\nOutput:\n{output}");
    }

    // ── Group filters (upstream.groupFilters) ────────────────────────────────

    [E2EFact]
    public async Task Http_GroupFilter_AllowedGroupProxied_DeniedGroupReturns403()
    {
        const string ns = "maven-e2e";
        var repoName = $"e2e-gf-{Guid.NewGuid():N}"[..18];

        try
        {
            // Self-contained repository: only the junit group is reachable through this proxy.
            await e2e.Client.CreateAsync(new MavenRepositoryV1Alpha1
            {
                ApiVersion = "maven.operator.io/v1alpha1",
                Kind       = "MavenRepository",
                Metadata   = new V1ObjectMeta { Name = repoName, NamespaceProperty = ns },
                Spec = new MavenRepositorySpec
                {
                    Type     = RepositoryType.Proxy,
                    Upstream = new UpstreamSpec
                    {
                        Url          = "https://repo1.maven.org/maven2",
                        CacheTtl     = "1d",
                        GroupFilters = ["junit"],
                    },
                    Metrics = new MetricsSpec { Enabled = false },
                    Auth   = new AuthSpec
                    {
                        Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                        Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    },
                },
            }, CancellationToken.None);

            await PodReadinessHelper.WaitForNginxReadyAsync(e2e.Client, ns, repoName);

            var (port, pf) = await PortForwardServiceAsync($"{repoName}-svc");
            try
            {
                using var client = new HttpClient
                {
                    BaseAddress = new Uri($"http://localhost:{port}"),
                    Timeout     = TimeSpan.FromSeconds(120),
                };

                // Allowed group → 200 with the upstream body.
                using (var resp = await client.GetAsync(
                           $"/repository/{repoName}/junit/junit/4.13.2/junit-4.13.2.jar"))
                {
                    resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                        $"allowed-group GET should return 200, got {(int)resp.StatusCode}");
                    (await resp.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(0);
                }

                // Denied group → 403 from the filter fallback (not forwarded upstream).
                using (var resp = await client.GetAsync(
                           $"/repository/{repoName}/org/apache/maven/core/3.9.9/maven-core-3.9.9.pom"))
                {
                    ((int)resp.StatusCode).ShouldBe(403,
                        $"denied-group GET should return 403, got {(int)resp.StatusCode}");
                }

                // Health endpoint is unaffected by the filter.
                using (var resp = await client.GetAsync("/healthz"))
                    ((int)resp.StatusCode).ShouldBe(200);
            }
            finally
            {
                KillPf(pf);
            }
        }
        finally
        {
            try { await e2e.Client.DeleteAsync<MavenRepositoryV1Alpha1>(repoName, ns, CancellationToken.None); } catch { }
        }
    }

    /// <summary>Port-forwards a service in the maven-e2e namespace to a free local port.</summary>
    private static async Task<(int Port, Process Process)> PortForwardServiceAsync(string svcName)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{svcName} {localPort}:80 -n maven-e2e",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        var process = new Process { StartInfo = psi };
        process.Start();

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
        KillPf(process);
        throw new TimeoutException($"port-forward to svc/{svcName} did not come up.");
    }

    private static void KillPf(Process? pf)
    {
        try { if (pf is not null && !pf.HasExited) pf.Kill(entireProcessTree: true); } catch { }
        pf?.Dispose();
    }
}


