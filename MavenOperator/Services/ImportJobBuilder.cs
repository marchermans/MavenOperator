using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using System.Text.Json;

namespace MavenOperator.Services;

/// <inheritdoc/>
public sealed class ImportJobBuilder(
    ILogger<ImportJobBuilder> logger)
    : IImportJobBuilder
{
    private const string TargetMountPath = "/data/target";
    private const string SourceMountPath = "/data/source";
    private const string CredsMountPath  = "/etc/import-credentials";
    private static string ImportJobServiceAccountName =>
        Environment.GetEnvironmentVariable("IMPORT_JOB_SERVICE_ACCOUNT_NAME") ?? "maven-operator-import";

    public Task<V1Job> BuildJobAsync(
        MavenRepositoryImportV1Alpha1 import,
        MavenRepositoryV1Alpha1 target,
        ResolvedTransferMode transferMode,
        string importJobImage,
        CancellationToken ct)
    {
        var importName = import.Metadata.Name!;
        var ns         = import.Metadata.NamespaceProperty!;
        var name       = $"{importName}-import-job";

        var env     = BuildEnvVars(import, target, transferMode);
        var volumes = BuildVolumes(import, target, transferMode);
        var mounts  = BuildVolumeMounts(import, transferMode);

        var container = new V1Container
        {
            Name            = "import-job",
            Image           = importJobImage,
            ImagePullPolicy = "IfNotPresent",
            Env             = env,
            VolumeMounts    = mounts,
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                    { ["cpu"] = new("250m"), ["memory"] = new("256Mi") },
                Limits = new Dictionary<string, ResourceQuantity>
                    { ["cpu"] = new("1"),    ["memory"] = new("512Mi") },
            },
        };

        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["maven.operator.io/import"]   = importName,
                    ["maven.operator.io/managed-by"] = importName,
                },
                OwnerReferences = [MakeOwnerRef(import)],
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 3,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string>
                        {
                            ["job-name"] = name,
                        },
                    },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy  = "OnFailure",
                        Containers     = [container],
                        Volumes        = volumes,
                        ServiceAccountName = ImportJobServiceAccountName,
                    },
                },
            },
        };

        logger.LogDebug(
            "Built import Job {JobName} for import {ImportName} (mode={TransferMode})",
            name, importName, transferMode);

        return Task.FromResult(job);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static List<V1EnvVar> BuildEnvVars(
        MavenRepositoryImportV1Alpha1 import,
        MavenRepositoryV1Alpha1 target,
        ResolvedTransferMode transferMode)
    {
        var spec   = import.Spec;
        var source = spec.Source;

        string importMode = source switch
        {
            { Api: { Type: ApiSourceType.Reposilite } } => "api-reposilite",
            { Api: { Type: ApiSourceType.JFrogCloud } } => "api-jfrog",
            { PvcSnapshot: not null }                    => "pvc-snapshot",
            { PvcLive: not null }                        => "pvc-live",
            _                                            => "api-reposilite",
        };

        var envs = new List<V1EnvVar>
        {
            Env("IMPORT_MODE",          importMode),
            Env("IMPORT_TRANSFER_MODE", transferMode == ResolvedTransferMode.DirectWrite ? "direct-write" : "http"),
            Env("IMPORT_OPTIONS_JSON",  JsonSerializer.Serialize(spec.Options)),
            Env("IMPORT_FILTERS_JSON",  JsonSerializer.Serialize(spec.Filters)),
            Env("TARGET_REPOSITORY",    spec.TargetRepository),
            Env("TARGET_NAMESPACE",     import.Metadata.NamespaceProperty!),
            Env("IMPORT_CR_NAME",       import.Metadata.Name!),
        };

        if (source.Api is { } api)
        {
            envs.Add(Env("SOURCE_URL",  api.Url));
            envs.Add(Env("SOURCE_REPO", api.Repository));
        }

        if (transferMode == ResolvedTransferMode.DirectWrite)
        {
            envs.Add(Env("TARGET_PVC_MOUNT", TargetMountPath));
        }
        else
        {
            // HTTP fallback — need service URL
            var targetSvc = $"{target.Metadata.Name}-svc";
            envs.Add(Env("TARGET_HTTP_URL", $"http://{targetSvc}/repository/{target.Metadata.Name}"));
        }

        if (source.PvcSnapshot is not null || source.PvcLive is not null)
        {
            envs.Add(Env("SOURCE_PVC_MOUNT",       SourceMountPath));
            envs.Add(Env("SOURCE_REPOSILITE_LAYOUT",
                (source.PvcSnapshot?.ReposiliteLayout ?? true).ToString().ToLower()));
            var subPath = source.PvcSnapshot?.SubPath ?? source.PvcLive?.SubPath ?? "";
            if (!string.IsNullOrEmpty(subPath))
                envs.Add(Env("SOURCE_SUBPATH", subPath));
        }

        if (source.Api?.CredentialsSecret is not null)
        {
            envs.Add(Env("CREDENTIALS_FILE", $"{CredsMountPath}/credentials.json"));
        }

        return envs;
    }

    private static List<V1Volume> BuildVolumes(
        MavenRepositoryImportV1Alpha1 import,
        MavenRepositoryV1Alpha1 target,
        ResolvedTransferMode transferMode)
    {
        var volumes = new List<V1Volume>();
        var spec    = import.Spec;

        // Target PVC — only for DirectWrite mode
        if (transferMode == ResolvedTransferMode.DirectWrite)
        {
            volumes.Add(new V1Volume
            {
                Name = "target-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = $"{target.Metadata.Name}-pvc",
                    ReadOnlyProperty = false,
                },
            });
        }

        // Source PVC for snapshot/live modes
        if (spec.Source.PvcSnapshot is { } snapshot)
        {
            volumes.Add(new V1Volume
            {
                Name = "source-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName        = snapshot.ClaimName,
                    ReadOnlyProperty = true,
                },
            });
        }
        else if (spec.Source.PvcLive is { } live)
        {
            volumes.Add(new V1Volume
            {
                Name = "source-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName        = live.ClaimName,
                    ReadOnlyProperty = true,
                },
            });
        }

        // API credentials Secret
        if (spec.Source.Api?.CredentialsSecret is { } credSecret)
        {
            volumes.Add(new V1Volume
            {
                Name   = "credentials",
                Secret = new V1SecretVolumeSource { SecretName = credSecret },
            });
        }

        return volumes;
    }

    private static List<V1VolumeMount> BuildVolumeMounts(
        MavenRepositoryImportV1Alpha1 import,
        ResolvedTransferMode transferMode)
    {
        var mounts = new List<V1VolumeMount>();
        var spec   = import.Spec;

        if (transferMode == ResolvedTransferMode.DirectWrite)
        {
            mounts.Add(new V1VolumeMount
            {
                Name      = "target-data",
                MountPath = TargetMountPath,
            });
        }

        if (spec.Source.PvcSnapshot is not null || spec.Source.PvcLive is not null)
        {
            var subPath = spec.Source.PvcSnapshot?.SubPath ?? spec.Source.PvcLive?.SubPath;
            mounts.Add(new V1VolumeMount
            {
                Name             = "source-data",
                MountPath        = SourceMountPath,
                ReadOnlyProperty = true,
                SubPath          = string.IsNullOrEmpty(subPath) ? null : subPath,
            });
        }

        if (spec.Source.Api?.CredentialsSecret is not null)
        {
            mounts.Add(new V1VolumeMount
            {
                Name             = "credentials",
                MountPath        = CredsMountPath,
                ReadOnlyProperty = true,
            });
        }

        return mounts;
    }

    private static V1EnvVar Env(string name, string value) => new() { Name = name, Value = value };

    private static V1OwnerReference MakeOwnerRef(MavenRepositoryImportV1Alpha1 import) =>
        new()
        {
            ApiVersion         = "maven.operator.io/v1alpha1",
            Kind               = "MavenRepositoryImport",
            Name               = import.Metadata.Name!,
            Uid                = import.Metadata.Uid,
            BlockOwnerDeletion = true,
            Controller         = true,
        };
}





