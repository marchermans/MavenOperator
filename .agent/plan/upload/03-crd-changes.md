# 03 — CRD Spec and Status Changes

This document details all changes required to the `MavenRepository` CRD to support uploads on Proxy and Virtual repositories.

---

## Existing AuthSpec (unchanged)

```csharp
public sealed class AuthSpec
{
    public AuthPolicySpec Download { get; set; } = new() { Policy = AuthPolicy.Anonymous };
    public AuthPolicySpec Upload   { get; set; } = new() { Policy = AuthPolicy.Authenticated };
}
```

The existing `auth.upload` policy continues to apply to **all** repository types that support uploads.
Its meaning is now: "how clients authenticate when uploading through this repo."

---

## Proxy Repository — New Upload Spec

### New Enum

```csharp
public enum ProxyUploadMode
{
    Passthrough,  // Enforce auth.upload policy; client must provide valid credentials
    Override      // Skip client auth entirely; accept uploads from anyone (migration mode)
}
```

### New Types

```csharp
public sealed class LocalObjectReference
{
    public string Name { get; set; } = default!;
    public string? Namespace { get; set; }  // optional, defaults to same namespace as CRD
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public sealed class ProxyUploadSpec
{
    /// <summary>
    /// Whether upload forwarding is enabled on this proxy.
    /// Default: false (read-only cache).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// How client authentication is handled for uploads.
    /// - Passthrough: enforce auth.upload policy (default).
    /// - Override: skip client auth entirely (migration mode).
    /// </summary>
    public ProxyUploadMode Mode { get; set; } = ProxyUploadMode.Passthrough;

    /// <summary>
    /// Credentials used to authenticate with the upstream Maven server for uploads.
    /// Required when Enabled is true.
    /// </summary>
    public LocalObjectReference? UpstreamCredentialsRef { get; set; }

    /// <summary>
    /// If true, allow uploads even if the proxy is externally exposed (LoadBalancer/Ingress).
    /// Default: false — uploads are blocked on externally exposed proxies for security.
    /// </summary>
    public bool ForceAllowOnExternal { get; set; } = false;
}
```

### Integration into ProxySpec

```csharp
public sealed class ProxySpec
{
    public string UpstreamUrl { get; set; } = default!;

    // Existing fields...
    public UpstreamAuthSpec? UpstreamAuth { get; set; }

    // NEW: upload forwarding configuration
    public ProxyUploadSpec Upload { get; set; } = new();
}
```

### Example CRD (Proxy with uploads)

```yaml
apiVersion: maven.operator.io/v1
kind: MavenRepository
metadata:
  name: nexus-migration-proxy
spec:
  type: Proxy
  auth:
    upload:
      policy: Authenticated   # used in Passthrough mode
      users:
        - secretRef: deployer-creds
          role: Deployer
  proxy:
    upstreamUrl: https://nexus.internal/repository/maven-releases/
    upstreamAuth:
      secretRef: nexus-reader-creds
    upload:
      enabled: true
      mode: Passthrough
      upstreamCredentialsRef:
        name: nexus-deployer-creds
```

---

## Virtual Repository — New Upload Spec

### New Types

```csharp
public sealed class VirtualUploadTarget
{
    /// <summary>
    /// Name of the member repository to upload to.
    /// Must match an entry in spec.virtual.members[].name.
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Optional per-target credentials override.
    /// If not set, falls back to the shared uploadCredentialsRef on VirtualUploadSpec.
    /// </summary>
    public LocalObjectReference? CredentialsRef { get; set; }
}

public sealed class VirtualUploadSpec
{
    /// <summary>
    /// List of member repositories that receive uploads.
    /// Empty list means uploads are disabled (405).
    /// Hosted members store directly; Proxy members forward to their upstream.
    /// Proxy targets require spec.proxy.upload.enabled == true on the target repo.
    /// </summary>
    public List<VirtualUploadTarget> Targets { get; set; } = new();

    /// <summary>
    /// Shared credentials used for all upload targets that don't have their own CredentialsRef.
    /// Required if any target doesn't specify its own credentials.
    /// </summary>
    public LocalObjectReference? SharedCredentialsRef { get; set;}
}
```

### Integration into VirtualSpec

```csharp
public sealed class VirtualMemberSpec
{
    public string Name { get; set; } = default!;
    public RepositoryType Type { get; set; }  // Hosted, Proxy
}

public sealed class VirtualSpec
{
    public List<VirtualMemberSpec> Members { get; set; } = new();

    // NEW: upload fan-out configuration
    public VirtualUploadSpec Upload { get; set; } = new();
}
```

### Example CRD (Virtual with uploads)

```yaml
apiVersion: maven.operator.io/v1
kind: MavenRepository
metadata:
  name: releases-virtual
spec:
  type: Virtual
  auth:
    upload:
      policy: Authenticated
      users:
        - secretRef: team-deployer-creds
          role: Deployer
  virtual:
    members:
      - name: releases-primary
        type: Hosted
      - name: releases-dr
        type: Hosted
      - name: central-proxy
        type: Proxy
    upload:
      targets:
        - name: releases-primary
        - name: releases-dr
          credentialsRef:
            name: dr-specific-deployer-creds  # optional per-target override
      sharedCredentialsRef:
        name: primary-deployer-creds  # used by releases-primary (no override)
```

---

## Status Changes — Upload Substatus

### New Types

```csharp
public sealed class ProxyUploadStatus
{
    public bool Enabled { get; set; }
    public ProxyUploadMode Mode { get; set; }
    public bool UpstreamCredentialsConfigured { get; set; }
    public string? LastSyncError { get; set; }
}

public sealed class VirtualUploadTargetStatus
{
    public string Name { get; set; } = default!;
    public bool Reachable { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastUploadSuccess { get; set; }
}

public sealed class VirtualUploadStatus
{
    public bool Enabled { get; set; }  // true if uploadTargets is non-empty
    public List<VirtualUploadTargetStatus> Targets { get; set; } = new();
}

public sealed class UploadStatus
{
    [JsonPropertyName("proxy")]
    public ProxyUploadStatus? Proxy { get; set; }

    [JsonPropertyName("virtual")]
    public VirtualUploadStatus? Virtual { get; set; }
}
```

### Integration into MavenRepositoryStatus

```csharp
public sealed class MavenRepositoryStatus
{
    public RepositoryPhase Phase { get; set; }
    public List<Condition> Conditions { get; set; } = new();

    // NEW: upload-specific status (only populated for Proxy/Virtual with uploads enabled)
    public UploadStatus? Upload { get; set; }
}
```

### Example Status (Proxy)

```yaml
status:
  phase: Running
  conditions: []
  upload:
    proxy:
      enabled: true
      mode: Passthrough
      upstreamCredentialsConfigured: true
      lastSyncError: null
```

### Example Status (Virtual)

```yaml
status:
  phase: Running
  conditions: []
  upload:
    virtual:
      enabled: true
      targets:
        - name: releases-primary
          reachable: true
          lastUploadSuccess: "2026-08-09T18:30:00Z"
        - name: releases-dr
          reachable: false
          lastError: "Connection refused — pod not running"
```

---

## New Condition Types

| Type | Reason | Meaning |
|------|--------|---------|
| `UploadForbidden` | `ExternallyExposedProxy` | Proxy has uploads enabled but is externally exposed without forceAllowOnExternal. |
| `UploadConfigurationError` | `MissingCredentialsSecret` | Referenced credential Secret does not exist or cannot be read. |
| `UploadConfigurationError` | `InvalidTargetMember` | Virtual upload target references a non-existent or non-Hosted member. |
| `UploadDegraded` | `TargetUnreachable` | One or more Virtual upload targets are unreachable (but others may work). |

---

## Admission Webhook Validation Rules

### Proxy Upload Validation

1. If `proxy.upload.enabled == true`:
   - `proxy.upload.upstreamCredentialsRef.name` must be non-empty.
   - Referenced Secret must exist and contain `username` + `password`.
   - If proxy is externally exposed AND `forceAllowOnExternal == false`, reject with clear error.

2. If `proxy.upload.mode == Passthrough`:
   - `auth.upload.policy` cannot be `Anonymous` (would defeat the purpose).

### Virtual Upload Validation

1. Each entry in `virtual.upload.targets[].name` must exist in `virtual.members[].name`.
2. Each target member must be either `Hosted` or `Proxy`.
3. If a target is `Proxy`, that Proxy repo's CRD must have `spec.proxy.upload.enabled == true`.
4. No duplicate names in `uploadTargets`.
5. At least one of:
   - Every target has its own `credentialsRef`, OR
   - `sharedCredentialsRef` is set for targets without overrides.

---

## Backward Compatibility

- All new fields have safe defaults (`enabled: false`, empty lists).
- Existing CRDs continue to work unchanged — uploads remain Hosted-only unless explicitly configured.
- No breaking changes to existing Spec or Status shapes.
