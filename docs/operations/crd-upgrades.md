# CRD upgrade procedure

Helm installs CRDs but intentionally does not upgrade them. Before a release
that changes a CRD, apply the versioned manifests explicitly:

```sh
kubectl apply -f deploy/helm/aegisscan/crds/
helm upgrade --install aegisscan deploy/helm/aegisscan
```

Validate with `kubectl explain` and inspect existing resources before changing
stored fields. Back up resource YAML for production upgrades.
