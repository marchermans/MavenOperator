using k8s;
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

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client     = new KubernetesClient(config);

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
        AuthPolicy uploadPolicy   = AuthPolicy.Authenticated,
        IEnumerable<string>? uploadSecretRefs   = null,
        IEnumerable<string>? downloadSecretRefs = null)
    {
        var repo = new MavenRepositoryV1Alpha1
        {
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = Namespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec
                    {
                        Policy     = downloadPolicy,
                        SecretRefs = downloadSecretRefs?.ToList() ?? [],
                    },
                    Upload = new AuthPolicySpec
                    {
                        Policy     = uploadPolicy,
                        SecretRefs = uploadSecretRefs?.ToList() ?? [],
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
}

[CollectionDefinition(ClusterCollection.CollectionName)]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture>
{
    public const string CollectionName = "Cluster";
}

