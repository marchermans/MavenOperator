using System.Net;
using System.Text;
using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;

namespace MavenOperator.Tests.E2E.Hosted;

/// <summary>
/// End-to-end tests for Hosted repositories.
///
/// Validates the complete user workflow:
///   1. Operator provisions a Hosted repo from a CRD (done in E2EFixture).
///   2. Maven/Gradle clients deploy artifacts via HTTP PUT (WebDAV).
///   3. Clients resolve artifacts via HTTP GET.
///   4. Auth policies are enforced correctly.
///
/// Run with: E2E_TESTS=true dotnet test --filter Category=E2E
/// Pre-requisite: operator running in cluster + E2E_REPO_BASE_URL pointing at the ingress.
/// </summary>
[Collection(E2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class HostedRepositoryE2ETests(E2EFixture e2e)
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // ── Operator provisions repository ────────────────────────────────────

    [E2EFact]
    public async Task Operator_MustCreateService_AccessibleOverHttp()
    {
        // E2EFixture already waited for the Service and set up a port-forward.
        // A GET to the health endpoint confirms NGINX started and the config rendered without errors.
        var response = await e2e.HttpClient.GetAsync("/healthz");
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"NGINX health check should return 2xx, got {(int)response.StatusCode}");
    }

    // ── HTTP / WebDAV upload ──────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Put_UploadArtifact_WithValidCredentials_Returns201Or204()
    {
        var content = Encoding.UTF8.GetBytes("fake-artifact-content-v1");
        var status  = await e2e.PutArtifactAsync(
            "io/mavenoperator/fixture/upload-test/1.0.0/upload-test-1.0.0.jar",
            content, e2e.UploadUser, e2e.UploadPassword);

        new[] { HttpStatusCode.Created, HttpStatusCode.NoContent }
            .ShouldContain(status, $"Upload with valid creds should return 201 or 204, got {status}");
    }

    [E2EFact]
    public async Task Http_Put_UploadArtifact_WithWrongCredentials_Returns401()
    {
        var status = await e2e.PutArtifactAsync(
            "io/mavenoperator/fixture/upload-auth-test/1.0.0/test-1.0.0.jar",
            Encoding.UTF8.GetBytes("content"), "wrong-user", "wrong-pass");

        status.ShouldBe(HttpStatusCode.Unauthorized,
            "Upload with incorrect credentials must be rejected with 401");
    }

    [E2EFact]
    public async Task Http_Put_UploadArtifact_WithNoCredentials_Returns401()
    {
        var status = await e2e.PutArtifactAsync(
            "io/mavenoperator/fixture/upload-anon-test/1.0.0/test-1.0.0.jar",
            Encoding.UTF8.GetBytes("content"), string.Empty, string.Empty);

        status.ShouldBe(HttpStatusCode.Unauthorized,
            "Upload without credentials must be rejected with 401");
    }

    // ── HTTP / WebDAV download ────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Get_DownloadArtifact_AfterUpload_WithValidCredentials_Returns200()
    {
        // Arrange: upload a known artifact
        var original = Encoding.UTF8.GetBytes("artifact-for-download-test");
        await e2e.PutArtifactAsync(
            "io/mavenoperator/fixture/download-test/1.0.0/download-test-1.0.0.jar",
            original, e2e.UploadUser, e2e.UploadPassword);

        // Act: download as the reader user
        var (status, body) = await e2e.GetArtifactAsync(
            "io/mavenoperator/fixture/download-test/1.0.0/download-test-1.0.0.jar",
            e2e.DownloadUser, e2e.DownloadPassword);

        // Assert: exact byte-for-byte match
        status.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull("Body should not be null for a 200 response");
        body!.ShouldBe(original, "Downloaded bytes must match what was uploaded — no corruption");
    }

    [E2EFact]
    public async Task Http_Get_DownloadArtifact_WithWrongCredentials_Returns401()
    {
        await e2e.PutArtifactAsync(
            "io/mavenoperator/fixture/dl-auth-test/1.0.0/test-1.0.0.jar",
            Encoding.UTF8.GetBytes("content"), e2e.UploadUser, e2e.UploadPassword);

        var (status, _) = await e2e.GetArtifactAsync(
            "io/mavenoperator/fixture/dl-auth-test/1.0.0/test-1.0.0.jar",
            "wrong", "creds");

        status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [E2EFact]
    public async Task Http_Get_NonExistentArtifact_Returns404()
    {
        var (status, _) = await e2e.GetArtifactAsync(
            "io/does/not/exist/1.0.0/exist-1.0.0.jar",
            e2e.DownloadUser, e2e.DownloadPassword);

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Content-type validation ───────────────────────────────────────────

    [E2EFact]
    public async Task Http_Get_DownloadJar_ReturnsApplicationOctetStreamOrJar()
    {
        var content = Encoding.UTF8.GetBytes("jar-bytes");
        await e2e.PutArtifactAsync(
            "io/mavenoperator/fixture/ct-test/1.0.0/ct-test-1.0.0.jar",
            content, e2e.UploadUser, e2e.UploadPassword);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{e2e.RepositoryUrl}/io/mavenoperator/fixture/ct-test/1.0.0/ct-test-1.0.0.jar");
        var cred = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{e2e.DownloadUser}:{e2e.DownloadPassword}"));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", cred);

        using var resp = await e2e.HttpClient.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        new[] { "application/octet-stream", "application/java-archive", "application/zip" }
            .ShouldContain(contentType,
                $"Unexpected Content-Type for a .jar: {contentType}");
    }

    // ── Maven client round-trip ───────────────────────────────────────────

    [E2EFact]
    public async Task Maven_Deploy_ThenResolve_ArtifactRoundtrip()
    {
        var fixtureDir = Path.Combine(FixturesDir, "maven-fixture");
        Directory.Exists(fixtureDir).ShouldBeTrue(
            $"Maven fixture dir not found: {fixtureDir}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Step 1: build and deploy to the operator-managed repository
        var (deployCode, deployOutput) = await e2e.RunMavenAsync(
            fixtureDir, "clean package deploy", ct: cts.Token);

        deployCode.ShouldBe(0,
            $"mvn deploy failed (exit {deployCode}):\n{deployOutput}");
        deployOutput.ShouldContain("BUILD SUCCESS");

        // Step 2: resolve from a fresh local repo — proves the artifact is in the operator repo
        var tmpLocalRepo = Path.Combine(Path.GetTempPath(), $"m2-{Guid.NewGuid():N}");
        var (resolveCode, resolveOutput) = await e2e.RunMavenAsync(
            fixtureDir,
            $"dependency:resolve -Dmaven.repo.local={tmpLocalRepo}",
            ct: cts.Token);

        resolveCode.ShouldBe(0,
            $"mvn dependency:resolve failed (exit {resolveCode}):\n{resolveOutput}");
        resolveOutput.ShouldContain("BUILD SUCCESS");
    }

    // ── Gradle client round-trip ──────────────────────────────────────────

    [E2EFact]
    public async Task Gradle_Publish_ThenResolve_ArtifactRoundtrip()
    {
        var fixtureDir = Path.Combine(FixturesDir, "gradle-fixture");
        Directory.Exists(fixtureDir).ShouldBeTrue(
            $"Gradle fixture dir not found: {fixtureDir}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Step 1: publish to the operator-managed repository
        var (publishCode, publishOutput) = await e2e.RunGradleAsync(
            fixtureDir, "build publish", ct: cts.Token);

        publishCode.ShouldBe(0,
            $"gradle publish failed (exit {publishCode}):\n{publishOutput}");
        publishOutput.ShouldContain("BUILD SUCCESSFUL");

        // Step 2: resolve dependencies (proves the artifact is retrievable)
        var (depsCode, depsOutput) = await e2e.RunGradleAsync(
            fixtureDir,
            "dependencies --configuration compileClasspath",
            ct: cts.Token);

        depsCode.ShouldBe(0,
            $"gradle dependencies failed (exit {depsCode}):\n{depsOutput}");
        depsOutput.ShouldContain("BUILD SUCCESSFUL");
    }
}

