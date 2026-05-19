using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.Controllers;

/// <summary>
/// Unit tests for <see cref="CredentialSecretController"/>.
/// </summary>
public sealed class CredentialSecretControllerTests
{
    private readonly IKubernetesClient _k8s = Substitute.For<IKubernetesClient>();
    private readonly IHostedRepositoryReconciler _hosted = Substitute.For<IHostedRepositoryReconciler>();
    private readonly IProxyRepositoryReconciler _proxy = Substitute.For<IProxyRepositoryReconciler>();
    private readonly IVirtualRepositoryReconciler _virtual = Substitute.For<IVirtualRepositoryReconciler>();
    private readonly IKubernetesEventService _events = Substitute.For<IKubernetesEventService>();
    private readonly CredentialSecretController _sut;

    public CredentialSecretControllerTests()
    {
        _sut = new CredentialSecretController(
            _k8s, _hosted, _proxy, _virtual, _events,
            NullLogger<CredentialSecretController>.Instance);
    }

    [Fact]
    public async Task ReconcileAsync_IgnoresSecret_WithoutCredentialLabel()
    {
        var secret = BuildSecret("my-secret", "ns", hasCredentialLabel: false);

        var result = await _sut.ReconcileAsync(secret, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // No list call should have been made
        await _k8s.DidNotReceive().ListAsync<MavenRepositoryV1Alpha1>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_IgnoresSecret_WhenLabelValueIsNotTrue()
    {
        var secret = BuildSecret("my-secret", "ns", hasCredentialLabel: true, labelValue: "false");

        await _sut.ReconcileAsync(secret, CancellationToken.None);

        await _k8s.DidNotReceive().ListAsync<MavenRepositoryV1Alpha1>(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_TriggersHostedRereconcile_WhenDownloadSecretRefMatches()
    {
        const string secretName = "deployer-creds";
        const string ns = "maven";

        var repo = BuildHostedRepo("my-repo", ns, downloadSecretRefs: [secretName]);
        _k8s.ListAsync<MavenRepositoryV1Alpha1>(ns, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([repo]);

        var secret = BuildSecret(secretName, ns, hasCredentialLabel: true);
        await _sut.ReconcileAsync(secret, CancellationToken.None);

        await _hosted.Received(1).ReconcileAsync(repo, Arg.Any<CancellationToken>());
        await _proxy.DidNotReceive().ReconcileAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_TriggersHostedRereconcile_WhenUploadSecretRefMatches()
    {
        const string secretName = "upload-creds";
        const string ns = "maven";

        var repo = BuildHostedRepo("my-repo", ns, uploadSecretRefs: [secretName]);
        _k8s.ListAsync<MavenRepositoryV1Alpha1>(ns, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([repo]);

        var secret = BuildSecret(secretName, ns, hasCredentialLabel: true);
        await _sut.ReconcileAsync(secret, CancellationToken.None);

        await _hosted.Received(1).ReconcileAsync(repo, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_TriggersProxyRereconcile_WhenUpstreamSecretRefMatches()
    {
        const string secretName = "upstream-creds";
        const string ns = "maven";

        var repo = BuildProxyRepo("my-proxy", ns, upstreamSecretRef: secretName);
        _k8s.ListAsync<MavenRepositoryV1Alpha1>(ns, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([repo]);

        var secret = BuildSecret(secretName, ns, hasCredentialLabel: true);
        await _sut.ReconcileAsync(secret, CancellationToken.None);

        await _proxy.Received(1).ReconcileAsync(repo, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotTrigger_WhenNoReposReferenceSecret()
    {
        const string ns = "maven";
        var repo = BuildHostedRepo("my-repo", ns, downloadSecretRefs: ["other-secret"]);
        _k8s.ListAsync<MavenRepositoryV1Alpha1>(ns, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([repo]);

        var secret = BuildSecret("unrelated-secret", ns, hasCredentialLabel: true);
        await _sut.ReconcileAsync(secret, CancellationToken.None);

        await _hosted.DidNotReceive().ReconcileAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_PublishesAuthUpdatedEvent_AfterSuccessfulRereconcile()
    {
        const string secretName = "creds";
        const string ns = "ns";
        var repo = BuildHostedRepo("repo", ns, downloadSecretRefs: [secretName]);
        _k8s.ListAsync<MavenRepositoryV1Alpha1>(ns, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([repo]);

        var secret = BuildSecret(secretName, ns, hasCredentialLabel: true);
        await _sut.ReconcileAsync(secret, CancellationToken.None);

        await _events.Received(1).PublishAsync(
            repo,
            "AuthUpdated",
            Arg.Is<string>(m => m.Contains(secretName)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_SwallowsException_FromSubReconcile_AndContinues()
    {
        const string secretName = "creds";
        const string ns = "ns";

        var repo1 = BuildHostedRepo("repo1", ns, downloadSecretRefs: [secretName]);
        var repo2 = BuildHostedRepo("repo2", ns, downloadSecretRefs: [secretName]);
        _k8s.ListAsync<MavenRepositoryV1Alpha1>(ns, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([repo1, repo2]);

        // First reconcile throws
        _hosted.ReconcileAsync(repo1, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new Exception("oops")));

        var secret = BuildSecret(secretName, ns, hasCredentialLabel: true);
        var result = await _sut.ReconcileAsync(secret, CancellationToken.None);

        // The controller as a whole should succeed
        result.IsSuccess.ShouldBeTrue();
        // Second repo should still be attempted
        await _hosted.Received(1).ReconcileAsync(repo2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletedAsync_ReturnsSuccess()
    {
        var secret = BuildSecret("any", "ns", hasCredentialLabel: true);
        var result = await _sut.DeletedAsync(secret, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static V1Secret BuildSecret(
        string name, string ns,
        bool hasCredentialLabel,
        string labelValue = "true") =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = ns,
                Labels = hasCredentialLabel
                    ? new Dictionary<string, string> { ["maven.operator.io/credential"] = labelValue }
                    : null,
            },
        };

    private static MavenRepositoryV1Alpha1 BuildHostedRepo(
        string name, string ns,
        IEnumerable<string>? downloadSecretRefs = null,
        IEnumerable<string>? uploadSecretRefs = null) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi" },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec
                    {
                        Policy     = downloadSecretRefs?.Any() == true ? AuthPolicy.Authenticated : AuthPolicy.Anonymous,
                        Users = (downloadSecretRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Reader }).ToList(),
                    },
                    Upload = new AuthPolicySpec
                    {
                        Policy     = uploadSecretRefs?.Any() == true ? AuthPolicy.Authenticated : AuthPolicy.Authenticated,
                        Users = (uploadSecretRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Deployer }).ToList(),
                    },
                },
            },
        };

    private static MavenRepositoryV1Alpha1 BuildProxyRepo(
        string name, string ns,
        string? upstreamSecretRef = null) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type     = RepositoryType.Proxy,
                Upstream = new UpstreamSpec
                {
                    Url  = "https://repo1.maven.org/maven2",
                    Auth = upstreamSecretRef is not null
                        ? new UpstreamAuthSpec { SecretRef = upstreamSecretRef }
                        : null,
                },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        };
}

