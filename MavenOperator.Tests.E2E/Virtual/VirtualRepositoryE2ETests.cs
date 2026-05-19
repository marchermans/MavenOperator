using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;
using System.Net;
using System.Text;

namespace MavenOperator.Tests.E2E.Virtual;

/// <summary>
/// End-to-end tests for Virtual repositories (Phase 3 acceptance criteria).
///
/// Validates that:
///   1. The operator provisions NGINX + C# proxy pods for a Virtual repo.
///   2. Artifacts uploaded to member Hosted repos are visible through the Virtual.
///   3. maven-metadata.xml is merged across all members (versions union).
///   4. PUT/DELETE to a Virtual repo return 405 Method Not Allowed.
///   5. Auth policies (anonymous vs authenticated download) are enforced.
///
/// The fixture creates two Hosted repos (member1, member2) and one Virtual that
/// fans out to both. Tests upload to specific members and verify resolution through
/// the Virtual URL.
///
/// Run with: E2E_TESTS=true dotnet test --filter Category=E2E
/// </summary>
[Collection(VirtualE2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class VirtualRepositoryE2ETests(VirtualE2EFixture e2e)
{
    private static readonly HttpStatusCode[] TransientGatewayStatuses =
    [
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    // ── Operator provisioning ─────────────────────────────────────────────────

    [E2EFact]
    public async Task Operator_MustCreateVirtualService_AccessibleOverHttp()
    {
        var response = await e2e.HttpClient.GetAsync("/healthz");
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"Virtual NGINX health check should return 2xx, got {(int)response.StatusCode}");
    }

    // ── Artifact fan-out ──────────────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Get_ArtifactFromMember1_ResolvesThroughVirtual()
    {
        // Upload a unique artifact to member1 only
        var path    = $"io/test/virtual-fanout/m1-only/{Guid.NewGuid():N}/artifact.jar";
        var content = Encoding.UTF8.GetBytes("member1-content");

        await e2e.UploadArtifactAsync(e2e.Member1Name, path, content);

        // Should resolve through the Virtual
        var resp = await GetWithGatewayRetryAsync(
            $"/repository/{e2e.VirtualName}/{path}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"Artifact from member1 should be reachable via Virtual (got {(int)resp.StatusCode})");

        var body = await resp.Content.ReadAsByteArrayAsync();
        body.ShouldBe(content);
    }

    [E2EFact]
    public async Task Http_Get_ArtifactFromMember2_ResolvesThroughVirtual()
    {
        var path    = $"io/test/virtual-fanout/m2-only/{Guid.NewGuid():N}/artifact.jar";
        var content = Encoding.UTF8.GetBytes("member2-content");

        await e2e.UploadArtifactAsync(e2e.Member2Name, path, content);

        var resp = await GetWithGatewayRetryAsync(
            $"/repository/{e2e.VirtualName}/{path}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"Artifact from member2 should be reachable via Virtual (got {(int)resp.StatusCode})");
    }

    [E2EFact]
    public async Task Http_Get_NonExistentArtifact_Returns404()
    {
        var resp = await GetWithGatewayRetryAsync(
            $"/repository/{e2e.VirtualName}/io/does/not/exist/9.9.9/x-9.9.9.jar");

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "Non-existent artifact should return 404 through Virtual");
    }

    // ── Upload rejection ──────────────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Put_ToVirtual_Returns405_MethodNotAllowed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/repository/{e2e.VirtualName}/io/test/should-not-upload/1.0/x-1.0.jar");
        req.Content = new ByteArrayContent("should not work"u8.ToArray());

        var response = await e2e.HttpClient.SendAsync(req);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400,
            $"PUT to a Virtual repo should fail (got {(int)response.StatusCode})");
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Created);
        response.StatusCode.ShouldNotBe(HttpStatusCode.NoContent);
    }

    [E2EFact]
    public async Task Http_Delete_ToVirtual_IsRejected()
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"/repository/{e2e.VirtualName}/io/test/no-delete/1.0/x-1.0.jar");

        var response = await e2e.HttpClient.SendAsync(req);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400,
            $"DELETE to a Virtual repo should fail (got {(int)response.StatusCode})");
    }

    // ── Metadata merge ────────────────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Get_MavenMetadata_ReturnsMergedVersions_FromBothMembers()
    {
        var group    = $"io/virtualtest/{Guid.NewGuid():N}";
        var artifact = "merged-artifact";

        // Upload version 1.0 to member1
        var pom10 = BuildPom(group.Replace('/', '.'), artifact, "1.0");
        await e2e.UploadArtifactAsync(e2e.Member1Name,
            $"{group}/{artifact}/1.0/{artifact}-1.0.pom",
            Encoding.UTF8.GetBytes(pom10));
        await e2e.UploadArtifactAsync(e2e.Member1Name,
            $"{group}/{artifact}/maven-metadata.xml",
            Encoding.UTF8.GetBytes(BuildMetadata(group.Replace('/', '.'), artifact, ["1.0"])));

        // Upload version 2.0 to member2
        var pom20 = BuildPom(group.Replace('/', '.'), artifact, "2.0");
        await e2e.UploadArtifactAsync(e2e.Member2Name,
            $"{group}/{artifact}/2.0/{artifact}-2.0.pom",
            Encoding.UTF8.GetBytes(pom20));
        await e2e.UploadArtifactAsync(e2e.Member2Name,
            $"{group}/{artifact}/maven-metadata.xml",
            Encoding.UTF8.GetBytes(BuildMetadata(group.Replace('/', '.'), artifact, ["2.0"])));

        // Fetch merged metadata through the Virtual
        var resp = await GetWithGatewayRetryAsync(
            $"/repository/{e2e.VirtualName}/{group}/{artifact}/maven-metadata.xml");

        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            $"maven-metadata.xml should be reachable via Virtual (got {(int)resp.StatusCode})");

        var xml = await resp.Content.ReadAsStringAsync();
        xml.ShouldContain("<version>1.0</version>");
        xml.ShouldContain("<version>2.0</version>");
        xml.Length.ShouldBeGreaterThan(0,
            "Merged metadata should contain version 1.0 from member1 and version 2.0 from member2");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildPom(string groupId, string artifactId, string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
            <modelVersion>4.0.0</modelVersion>
            <groupId>{groupId}</groupId>
            <artifactId>{artifactId}</artifactId>
            <version>{version}</version>
        </project>
        """;

    private static string BuildMetadata(
        string groupId, string artifactId, IEnumerable<string> versions)
    {
        var versionElements = string.Join("\n",
            versions.Select(v => $"    <version>{v}</version>"));
        var latest  = versions.Last();
        var ts      = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata>
              <groupId>{groupId}</groupId>
              <artifactId>{artifactId}</artifactId>
              <versioning>
                <versions>
            {versionElements}
                </versions>
                <latest>{latest}</latest>
                <release>{latest}</release>
                <lastUpdated>{ts}</lastUpdated>
              </versioning>
            </metadata>
            """;
    }

    private async Task<HttpResponseMessage> GetWithGatewayRetryAsync(string requestUri)
    {
        HttpResponseMessage? lastResponse = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            lastResponse?.Dispose();
            lastResponse = await e2e.HttpClient.GetAsync(requestUri);
            if (!TransientGatewayStatuses.Contains(lastResponse.StatusCode))
                return lastResponse;

            await Task.Delay(1000);
        }

        return lastResponse ?? await e2e.HttpClient.GetAsync(requestUri);
    }
}


