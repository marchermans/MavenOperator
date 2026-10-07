using System;
using Xunit;

namespace MavenOperator.Tests.Integration.Infrastructure;

/// <summary>
/// Like <see cref="IntegrationFactAttribute"/> but additionally requires
/// IMPORT_DIRECT_WRITE_TESTS=true. Direct-write import tests need the import Job's
/// pod and the target repository's NGINX to share one RWO volume on the same node,
/// which only holds on a single-node k3d cluster (K3D_AGENTS=0). run-tests.sh sets
/// this automatically for self-managed clusters; externally-provided multi-node
/// clusters must opt in explicitly.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ImportDirectWriteFactAttribute : FactAttribute
{
    public ImportDirectWriteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INTEGRATION_TESTS") != "true" ||
            Environment.GetEnvironmentVariable("IMPORT_DIRECT_WRITE_TESTS") != "true")
        {
            Skip = "Requires INTEGRATION_TESTS=true and IMPORT_DIRECT_WRITE_TESTS=true";
        }
    }
}
