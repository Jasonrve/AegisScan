# Installation

The Helm chart is under `deploy/helm/aegisscan`; CRDs are under
`deploy/crds`. Review and install CRDs first, then install the chart with an
image and cache settings appropriate for the cluster:

```powershell
kubectl apply -f deploy/crds
helm upgrade --install aegisscan deploy/helm/aegisscan --namespace aegisscan --create-namespace
```

The chart creates the API Service, ServiceAccount/RBAC, health probes,
optional cache PVC, and a restrictive NetworkPolicy. PostgreSQL and OIDC
credentials are expected to be supplied through deployment configuration or
referenced Secrets; raw credentials are not written to CRD status.
