# Installation

The Helm chart is under `deploy/helm/aegisscan`; CRDs are under
`deploy/crds`. Review and install CRDs first. The operator needs a
PostgreSQL connection string; create the Secret it reads before installing
the chart:

```powershell
kubectl apply -f deploy/crds
kubectl create secret generic aegisscan-database --namespace aegisscan `
  --from-literal=connection-string="Host=postgresql;Port=5432;Database=aegisscan;Username=aegisscan;Password=<password>"
helm upgrade --install aegisscan deploy/helm/aegisscan --namespace aegisscan --create-namespace
```

The Secret name/key are configurable via `postgresql.existingSecret` /
`postgresql.secretKey`; set `postgresql.enabled: false` to omit this wiring
(for example, to inject the connection string through `env` instead). This
chart does not deploy PostgreSQL itself — point it at an existing instance.

The chart also creates the API Service, ServiceAccount/RBAC, health probes,
and a restrictive NetworkPolicy. OIDC is on the architecture roadmap (see
`docs/architecture`) but not wired into the operator yet.
