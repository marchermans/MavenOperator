using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

public sealed class KubernetesResourceManagerTopologyTests
{
    private static MavenRepositoryV1Alpha1 BuildHostedEntity(string name, string ns)
        => new()
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind = "MavenRepository",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Uid = "uid-123",
            },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = true },
            },
        };

    [Fact]
    public async Task EnsureDeploymentAsync_InjectsTopologySpreadConstraint_WhenConfigured()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");

        var topologyConfig = new TopologySpreadConstraintConfig
        {
            TopologyKey = "kubernetes.io/hostname",
            MaxSkew = 1,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.ScheduleAnyway,
        };

        // Mock: deployment doesn't exist yet — return null to trigger creation path
        client.GetAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((V1Deployment?)null);

        // Capture the created deployment object
        V1Deployment? captured = null;
        client.CreateAsync(Arg.Do<V1Deployment>(d => captured = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "abc123",
            new V1PodSpec { Containers = [] }, 3, CancellationToken.None,
            topologyConfig);

        // Assert — creation path
        captured.ShouldNotBeNull();
        captured.Spec!.Replicas.ShouldBe(3);
        captured.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        captured.Spec.Template.Spec.TopologySpreadConstraints.Count.ShouldBe(1);
        captured.Spec.Template.Spec.TopologySpreadConstraints[0].TopologyKey.ShouldBe("kubernetes.io/hostname");
        captured.Spec.Template.Spec.TopologySpreadConstraints[0].MaxSkew.ShouldBe(1);
        captured.Spec.Template.Spec.TopologySpreadConstraints[0].WhenUnsatisfiable.ShouldBe("ScheduleAnyway");
    }

    [Fact]
    public async Task EnsureDeploymentAsync_NoTopologySpreadConstraint_WhenNotProvided()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");

        // Mock: deployment doesn't exist
        client.GetAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((V1Deployment?)null);

        V1Deployment? captured = null;
        client.CreateAsync(Arg.Do<V1Deployment>(d => captured = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act — no topology config
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "abc123",
            new V1PodSpec { Containers = [] }, 1, CancellationToken.None);

        // Assert
        captured.ShouldNotBeNull();
        (captured.Spec!.Template!.Spec!.TopologySpreadConstraints ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task EnsureDeploymentAsync_UsesDefaultTopologyKey_WhenOmitted()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");

        var topologyConfig = new TopologySpreadConstraintConfig
        {
            MaxSkew = 1,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.ScheduleAnyway,
            // TopologyKey is null — should default to kubernetes.io/hostname
        };

        client.GetAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((V1Deployment?)null);

        V1Deployment? captured = null;
        client.CreateAsync(Arg.Do<V1Deployment>(d => captured = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "abc123",
            new V1PodSpec { Containers = [] }, 1, CancellationToken.None,
            topologyConfig);

        // Assert
        captured.ShouldNotBeNull();
        captured.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        captured.Spec.Template.Spec.TopologySpreadConstraints[0].TopologyKey.ShouldBe("kubernetes.io/hostname");
    }

    [Fact]
    public async Task EnsureDeploymentAsync_HandlesUpdatePath_WhenDeploymentExists()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");

        var topologyConfig = new TopologySpreadConstraintConfig
        {
            TopologyKey = "topology.kubernetes.io/zone",
            MaxSkew = 2,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.DoNotSchedule,
        };

        // Mock: existing deployment (no topology constraint yet)
        // NOTE: must use explicit value assignments (new List) — the k8s library
        // returns null from these computed properties, and collection initializers
        // (= []) call the getter then .Add() on null → NRE.
        var existing = new V1Deployment { Metadata = new V1ObjectMeta { Name = "test-repo-nginx" } };
        existing.Spec = new V1DeploymentSpec { Replicas = 1 };
        existing.Spec.Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = "test-repo-nginx" } };
        existing.Spec.Template = new V1PodTemplateSpec { Metadata = new V1ObjectMeta { Annotations = new Dictionary<string, string>() } };
        existing.Spec.Template.Spec = new V1PodSpec { Containers = new List<V1Container>(), TopologySpreadConstraints = new List<V1TopologySpreadConstraint>() };

        client.GetAsync<V1Deployment>("test-repo-nginx", "default", Arg.Any<CancellationToken>())
            .Returns(existing);

        V1Deployment? updated = null;
        client.UpdateAsync(Arg.Do<V1Deployment>(d => updated = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "configHash",
            existing.Spec.Template.Spec, 2, CancellationToken.None,
            topologyConfig);

        // Assert — update path
        updated.ShouldNotBeNull();
        updated.Spec!.Replicas.ShouldBe(2);
        updated.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        updated.Spec.Template.Spec.TopologySpreadConstraints.Count.ShouldBe(1);
        updated.Spec.Template.Spec.TopologySpreadConstraints[0].TopologyKey.ShouldBe("topology.kubernetes.io/zone");
        updated.Spec.Template.Spec.TopologySpreadConstraints[0].MaxSkew.ShouldBe(2);
        updated.Spec.Template.Spec.TopologySpreadConstraints[0].WhenUnsatisfiable.ShouldBe("DoNotSchedule");
    }

    [Fact]
    public async Task EnsureDeploymentAsync_AppendsConstraint_ToExistingList()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");

        var topologyConfig = new TopologySpreadConstraintConfig
        {
            TopologyKey = "kubernetes.io/hostname",
            MaxSkew = 1,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.ScheduleAnyway,
        };

        // Mock: existing deployment with 1 existing constraint
        var existing = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = "test-repo-nginx" },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = "test-repo-nginx" } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Annotations = new Dictionary<string, string>() },
                    Spec = new V1PodSpec
                    {
                        Containers = [],
                        TopologySpreadConstraints =
                        new List<V1TopologySpreadConstraint>
                        {
                            new V1TopologySpreadConstraint
                            {
                                TopologyKey = "other-key",
                                MaxSkew = 1,
                                WhenUnsatisfiable = "ScheduleAnyway",
                            },
                        },
                    },
                },
            },
        };

        client.GetAsync<V1Deployment>("test-repo-nginx", "default", Arg.Any<CancellationToken>())
            .Returns(existing);

        V1Deployment? updated = null;
        client.UpdateAsync(Arg.Do<V1Deployment>(d => updated = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "configHash",
            existing.Spec.Template.Spec, 1, CancellationToken.None,
            topologyConfig);

        // Assert — existing constraint preserved + new one appended
        updated.ShouldNotBeNull();
        updated.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        updated.Spec.Template.Spec.TopologySpreadConstraints.Count.ShouldBe(2);
        updated.Spec.Template.Spec.TopologySpreadConstraints[0].TopologyKey.ShouldBe("other-key");
        updated.Spec.Template.Spec.TopologySpreadConstraints[1].TopologyKey.ShouldBe("kubernetes.io/hostname");
    }

    [Fact]
    public async Task EnsureDeploymentAsync_DefaultReplicaCount_WhenSpecReplicasIsNull()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");
        owner.Spec.Replicas = null; // not set

        client.GetAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((V1Deployment?)null);

        V1Deployment? captured = null;
        client.CreateAsync(Arg.Do<V1Deployment>(d => captured = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act — replicas: 1 is the default
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "abc123",
            new V1PodSpec { Containers = [] }, 1, CancellationToken.None);

        // Assert
        captured.ShouldNotBeNull();
        captured.Spec!.Replicas.ShouldBe(1);
    }

    [Fact]
    public async Task EnsureDeploymentAsync_SingleHashOverload_ForwardsTopologyConfig()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("test-repo", "default");

        var topologyConfig = new TopologySpreadConstraintConfig
        {
            TopologyKey = "kubernetes.io/hostname",
            MaxSkew = 1,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.ScheduleAnyway,
        };

        client.GetAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((V1Deployment?)null);

        V1Deployment? captured = null;
        client.CreateAsync(Arg.Do<V1Deployment>(d => captured = d), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Deployment>());

        // Act — uses the single-hash overload (backwards compatible)
        await sut.EnsureDeploymentAsync(
            owner, "test-repo-nginx", "singleHash",
            new V1PodSpec { Containers = [] }, 1, CancellationToken.None,
            topologyConfig);

        // Assert
        captured.ShouldNotBeNull();
        captured.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        captured.Spec.Template.Spec.TopologySpreadConstraints.Count.ShouldBe(1);
    }
}
