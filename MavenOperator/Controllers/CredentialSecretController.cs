using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;

namespace MavenOperator.Controllers;

/// <summary>
/// Watches Kubernetes Secrets labelled with <c>maven.operator.io/credential=true</c>.
/// When a credential Secret changes, finds all MavenRepositories that reference it
/// and triggers re-reconciliation so the htpasswd files are rebuilt immediately.
/// </summary>
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch)]
[EntityRbac(typeof(MavenRepositoryV1Alpha1), Verbs = RbacVerb.All)]
public sealed class CredentialSecretController(
    IKubernetesClient k8s,
    IHostedRepositoryReconciler hostedReconciler,
    IProxyRepositoryReconciler proxyReconciler,
    IVirtualRepositoryReconciler virtualReconciler,
    IKubernetesEventService events,
    ILogger<CredentialSecretController> logger)
    : IEntityController<V1Secret>
{
    private const string CredentialLabel = "maven.operator.io/credential";

    public async Task<ReconciliationResult<V1Secret>> ReconcileAsync(
        V1Secret entity,
        CancellationToken cancellationToken)
    {
        // Only handle Secrets explicitly labelled as credential Secrets.
        if (entity.Metadata.Labels?.TryGetValue(CredentialLabel, out var labelVal) != true
            || labelVal != "true")
            return ReconciliationResult<V1Secret>.Success(entity);

        var secretName = entity.Metadata.Name!;
        var secretNs   = entity.Metadata.NamespaceProperty!;

        logger.LogInformation(
            "Credential Secret {Namespace}/{Name} changed — finding affected MavenRepositories",
            secretNs, secretName);

        // List all MavenRepository resources in the same namespace.
        var repos = await k8s.ListAsync<MavenRepositoryV1Alpha1>(
            secretNs,
            cancellationToken: cancellationToken);

        // Filter to repos that reference this Secret in any auth policy or upstream auth.
        var affected = repos
            .Where(r =>
                r.Spec.Auth.Download.Users.Any(u => u.SecretRef == secretName) ||
                r.Spec.Auth.Upload.Users.Any(u => u.SecretRef == secretName) ||
                r.Spec.Upstream?.Auth?.SecretRef == secretName)
            .ToList();

        if (affected.Count == 0)
        {
            logger.LogDebug(
                "Credential Secret {Namespace}/{Name} changed but no MavenRepositories reference it",
                secretNs, secretName);
            return ReconciliationResult<V1Secret>.Success(entity);
        }

        foreach (var repo in affected)
        {
            var repoNs   = repo.Metadata.NamespaceProperty!;
            var repoName = repo.Metadata.Name!;
            logger.LogInformation(
                "Re-reconciling MavenRepository {Namespace}/{Name} due to credential Secret '{SecretName}' change",
                repoNs, repoName, secretName);
            try
            {
                await (repo.Spec.Type switch
                {
                    RepositoryType.Hosted  => hostedReconciler.ReconcileAsync(repo, cancellationToken),
                    RepositoryType.Proxy   => proxyReconciler.ReconcileAsync(repo, cancellationToken),
                    RepositoryType.Virtual => virtualReconciler.ReconcileAsync(repo, cancellationToken),
                    _                      => Task.CompletedTask,
                });

                await events.PublishAsync(repo, "AuthUpdated",
                    $"Credential Secret '{secretName}' changed — htpasswd rebuilt",
                    ct: cancellationToken);
            }
            catch (Exception ex)
            {
                // Log but don't propagate — other repos still need to be re-reconciled.
                logger.LogError(ex,
                    "Failed to re-reconcile MavenRepository {Namespace}/{Name} after credential Secret change",
                    repoNs, repoName);
            }
        }

        return ReconciliationResult<V1Secret>.Success(entity);
    }

    public Task<ReconciliationResult<V1Secret>> DeletedAsync(
        V1Secret entity,
        CancellationToken cancellationToken)
    {
        // No specific cleanup needed when a credential Secret is deleted.
        // The next reconcile of any affected MavenRepository will surface the error.
        return Task.FromResult(ReconciliationResult<V1Secret>.Success(entity));
    }
}


