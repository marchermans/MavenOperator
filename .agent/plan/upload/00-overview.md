# Upload Support for Proxy and Virtual Repositories

## Problem Statement

Currently, only **Hosted** repositories support artifact uploads (PUT/DELETE via WebDAV).
Proxy repositories are read-only caches, and Virtual repositories explicitly reject writes with 405.

This blocks two real-world scenarios:

1. **Migrating from legacy Maven to the operator-managed setup** — teams want to push artifacts
   through a Proxy endpoint that forwards them upstream (e.g., Nexus/Artifactory) using fixed
   server credentials, without exposing the proxy externally.

2. **Deploying to Virtual repositories** — users expect `mvn deploy` against a Virtual URL to
   fan out to selected upstream Hosted members, just like downloads fan in from all members.

## Goals

- Enable uploads through Proxy repositories with fixed upstream credentials.
- Enable uploads through Virtual repositories using the same auth model as Hosted repos,
  forwarding credentials to a configurable subset of member repositories (Hosted or Proxy).
- Preserve backward compatibility — existing behavior remains unchanged unless explicitly configured.
- Keep CRD as single source of truth; no hardcoded upload targets.

## Non-Goals (for now)

- Dynamic credential mapping between client users and upstream users.
- Upload retries with exponential backoff beyond standard HTTP retry semantics.
- Cross-namespace Virtual member uploads (all members must be in the same namespace).

---

## Design Documents

| File | Topic |
|------|-------|
| [01-proxy-upload.md](./01-proxy-upload.md) | Proxy repository upload forwarding |
| [02-virtual-upload.md](./02-virtual-upload.md) | Virtual repository upload fan-out |
| [03-crd-changes.md](./03-crd-changes.md) | CRD spec and status changes |
| [04-implementation-plan.md](./04-implementation-plan.md) | Phased implementation steps |
