# MavenOperator — MVP Plan Overview

## Vision

A Kubernetes Operator that manages self-hosted Maven repositories using a **Custom Resource Definition (CRD)** as the single source of truth. Operators watch CRDs and reconcile the underlying infrastructure (NGINX + C# proxy services) to match the desired state.

The end user experience is simple:
1. Apply a YAML manifest describing a Maven repository (or repository group).
2. The operator provisions, configures, and exposes it automatically.
3. Update the manifest → the operator reconciles the change.

---

## High-Level Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  Kubernetes Cluster                                              │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │  MavenOperator (C# .NET 10 Controller)                      │ │
│  │  - Watches MavenRepository CRDs                             │ │
│  │  - Reconciles desired vs actual state                       │ │
│  │  - Manages child resources (Deployments, Services, Secrets) │ │
│  └──────────────────┬──────────────────────────────────────────┘ │
│                     │ creates/updates/deletes                    │
│         ┌───────────┴────────────────────────────────┐           │
│         │                                            │           │
│  ┌──────▼──────────────┐              ┌──────────────▼────────┐  │
│  │  Hosted Repository  │              │  Virtual Repository   │  │
│  │  (NGINX)            │              │  (C# Proxy + NGINX)   │  │
│  │  - Stores artifacts │              │  - Fans out to N repos│  │
│  │  - Auth optional    │              │  - Merges metadata    │  │
│  └─────────────────────┘              └───────────────────────┘  │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │  PersistentVolumeClaims (artifact storage per hosted repo)  │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

---

## Repository Types

| Type | Description | Implementation |
|------|-------------|----------------|
| `Hosted` | Stores artifacts locally | NGINX + WebDAV module |
| `Proxy` | Caches artifacts from a remote upstream | NGINX proxy_pass + local cache |
| `Virtual` | Aggregates multiple Hosted/Proxy repos | C# Aggregation Proxy (merges maven-metadata.xml) |

---

## Document Index

| # | Document | Topic |
|---|----------|-------|
| 01 | [CRD Design](./01-crd-design.md) | CRD schema, examples |
| 02 | [Operator Architecture](./02-operator-architecture.md) | Controller logic, reconciliation loop |
| 03 | [Repository Types](./03-repository-types.md) | Hosted, Proxy, Virtual — deep dives |
| 04 | [Authentication](./04-authentication.md) | Auth strategies, Kubernetes Secrets |
| 05 | [Virtual Repo & Metadata Merging](./05-virtual-repo-metadata.md) | maven-metadata.xml aggregation |
| 06 | [NGINX Configuration](./06-nginx-configuration.md) | NGINX templates per repo type |
| 07 | [C# Aggregation Proxy](./07-csharp-aggregation-proxy.md) | Virtual repo proxy design |
| 08 | [Storage](./08-storage.md) | PVC strategy, volume mounts |
| 09 | [Milestones](./09-milestones.md) | Phased delivery plan |

