# 14 — Gateway API Support

## Overview

The operator supports **two** mechanisms for exposing a `MavenRepository` externally:

| Mechanism | Field | Created resource |
|-----------|-------|-----------------|
| Classic Ingress | `spec.ingress` | `networking.k8s.io/v1 Ingress` |
| Kubernetes Gateway API | `spec.gateway` | `gateway.networking.k8s.io/v1 HTTPRoute` |

Both fields may **not** be enabled simultaneously — this is enforced by a CEL validation rule.

---

## spec.gateway Sub-spec

```yaml
spec:
  gateway:
    enabled: true
    # Reference to a Gateway resource that must already exist in the cluster.
    # The operator does NOT create the Gateway itself (that is infrastructure concern).
    gatewayRef:
      name: prod-gateway           # required
      namespace: infra-gateways   # optional; defaults to same namespace as MavenRepository
      sectionName: https           # optional; targets a specific listener
    # Hostname to add to the HTTPRoute
    hostname: maven.example.com   # optional; falls back to * if absent
    # URL path prefix for the HTTPRoute match
    path: /repository/my-releases  # optional; defaults to /repository/{name}
    # TLS terminated upstream — if true the BackendRef uses HTTPS port
    tlsSecretRef: maven-tls        # optional; name of a Secret in the same namespace
    # Allowed HTTP methods on the route (PUT/DELETE blocked for Virtual repos via CEL)
    # Operator always sets this automatically — users should not set it.
    # Additional labels/annotations to merge into the generated HTTPRoute
    routeLabels: {}
    routeAnnotations: {}
```

---

## Child Resources

| Suffix | Kind | Purpose |
|--------|------|---------|
| `-route` | `gateway.networking.k8s.io/v1 HTTPRoute` | Gateway API route (when `spec.gateway.enabled: true`) |
| `-ing` | `networking.k8s.io/v1 Ingress` | Classic ingress (when `spec.ingress.enabled: true`) |

All child resources carry an owner reference to the parent `MavenRepository`.

---

## HTTPRoute Design

### Hosted / Proxy repos

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: <name>-route
  namespace: <namespace>
  ownerReferences: [...]
spec:
  parentRefs:
    - name: prod-gateway
      namespace: infra-gateways    # from spec.gateway.gatewayRef.namespace
      sectionName: https           # from spec.gateway.gatewayRef.sectionName (if set)
  hostnames:
    - maven.example.com            # from spec.gateway.hostname (if set)
  rules:
    - matches:
        - path:
            type: PathPrefix
            value: /repository/my-releases
      backendRefs:
        - name: <name>-svc
          port: 80
```

### Virtual repos — block uploads at the route level

Virtual repositories must return `405 Method Not Allowed` for PUT and DELETE.
The operator adds a **second rule** that matches PUT/DELETE and returns a fixed 405 response
using the Gateway API `HTTPRouteFilter` of type `RequestRedirect` with status 405, or
preferably an `ExtensionRef` to a `ResponseOverride` policy if the gateway supports it.

If the gateway implementation does **not** support response overrides, the operator falls
back to a NGINX annotation on the Service side (same behaviour as the classic Ingress path).

---

## CEL Validation Rules (additions to CRD)

```
# Cannot enable both ingress and gateway at the same time
- rule: "!(self.ingress.enabled && self.gateway.enabled)"
  message: "spec.ingress and spec.gateway cannot both be enabled"

# Gateway enabled → gatewayRef.name must be present
- rule: "!self.gateway.enabled || self.gateway.gatewayRef.name != ''"
  message: "spec.gateway.gatewayRef.name is required when gateway is enabled"
```

---

## RBAC

The operator's `ClusterRole` (or namespaced `Role`) must include:

```yaml
- apiGroups: ["gateway.networking.k8s.io"]
  resources: ["httproutes"]
  verbs: ["get", "list", "watch", "create", "update", "patch", "delete"]
```

Add this block to `charts/maven-operator/templates/clusterrole.yaml`.

---

## Reconciler Steps (GatewayReconciler)

1. If `spec.gateway.enabled == false` → ensure no `-route` HTTPRoute exists (delete if present). Done.
2. Validate `spec.gateway.gatewayRef.name` is non-empty.
3. Build the `HTTPRoute` object from spec.
4. For Virtual repos, add the 405 rule.
5. Server-side apply the `HTTPRoute` with owner reference.
6. Update `status.url` from the HTTPRoute hostname + path.

---

## Status

`status.url` is set to `https://<hostname><path>` (or `http://` if no TLS secret is given)
whether the route is exposed via Ingress or Gateway.

---

## Delivery checklist

- [ ] `GatewaySpec.cs` entity class (C#)
- [ ] `spec.gateway` section added to CRD YAML + Helm chart CRDs
- [ ] CEL rules for mutual exclusion + required `gatewayRef.name`
- [ ] `GatewayRouteReconciler` service / reconciler step
- [ ] RBAC for `httproutes` in Helm `ClusterRole` template
- [ ] Update `01-crd-design.md` child-resource table (add `-route` row)
- [ ] Unit tests: `GatewayRouteReconciler` — enabled/disabled, Hosted/Virtual, missing gatewayRef
- [ ] Integration tests: apply CRD + operator → assert HTTPRoute created/deleted on toggle
- [ ] E2E tests: Maven client resolves through Gateway API HTTPRoute

