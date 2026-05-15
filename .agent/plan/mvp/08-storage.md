# 08 — Storage
## Hosted Repositories
Each `Hosted` MavenRepository gets a dedicated `PersistentVolumeClaim`.
```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: my-releases-pvc
  namespace: maven
  ownerReferences:
    - apiVersion: maven.operator.io/v1alpha1
      kind: MavenRepository
      name: my-releases
      uid: ...
      blockOwnerDeletion: true
      controller: true
spec:
  accessModes: [ReadWriteOnce]    # ReadWriteMany if NFS/CephFS is available
  storageClassName: standard
  resources:
    requests:
      storage: 50Gi
```
> ⚠️ PVCs are **not deleted** when a MavenRepository CRD is deleted. The `ownerReference` does NOT include `blockOwnerDeletion` on PVCs by default because orphaning artifacts is safer than automatic deletion. A separate `spec.storage.deletionPolicy: Retain | Delete` field (default `Retain`) controls this.
---
## Proxy Cache
Proxy repositories use an `emptyDir` volume for the NGINX proxy cache by default (cache is ephemeral, rebuilt on restart). A `spec.cache` field can optionally specify a PVC for persistent cache:
```yaml
cache:
  persistent: true
  size: 10Gi
```
---
## Volume Mount Layout
```
NGINX container:
  /var/maven/repository/     ← PVC (Hosted) or not mounted (Proxy/Virtual)
  /var/cache/nginx/<name>/   ← emptyDir (Proxy cache)
  /etc/nginx/conf.d/         ← ConfigMap (nginx config)
  /etc/nginx/auth/           ← Secret (htpasswd)
```
---
## Backup Considerations (Out of MVP scope)
- PVCs can be snapshotted using `VolumeSnapshot` if the storage class supports it.
- A future `MavenRepositoryBackup` CRD could trigger scheduled snapshots.
