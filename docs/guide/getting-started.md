# Getting started

Two ways to try AegisScan: the **local stack** (Docker Compose, everything included, about 5 minutes) or a **cluster install** with Helm.

## Option A: local stack with Docker Compose

Needs Docker (or Rancher Desktop) with roughly 6 GB RAM free. Compose starts PostgreSQL, Keycloak (OIDC), a throw-away k3s cluster (where scan Jobs run), the operator/API and the web console.

```bash
git clone https://github.com/Jasonrve/AegisScan.git
cd AegisScan
docker compose up --build -d
```

Open `http://localhost:3000` and sign in with one of the seeded demo users:

| User | Password | Role |
|---|---|---|
| `admin` | `admin123` | Admin |
| `operator` | `operator123` | Operator |
| `viewer` | `viewer123` | Viewer |

Demo data is seeded (`AEGIS_SEED_DEMO=true`). To scan real repositories use **Repositories → Add repository**, then **Run scan**. Stop with `docker compose down` (add `-v` only to delete data).

::: warning Demo credentials
The Keycloak realm in `deploy/dev` is for local use only. Never reuse it outside your machine.
:::

## Option B: install on Kubernetes with Helm

Prerequisites: Kubernetes 1.24 or newer. PostgreSQL and an OIDC provider (Keycloak) can be bundled with the chart (`postgresql.enabled`, `keycloak.enabled`, see [Installation](/operations/installation#bundled-dependencies-subcharts)) or you can bring your own.

```bash
# 1. CRDs (Helm does not upgrade CRDs, so install them explicitly)
RAW=https://raw.githubusercontent.com/Jasonrve/AegisScan/main/deploy/crds
kubectl apply -f $RAW/aegisscan.io_repositories.yaml \
              -f $RAW/aegisscan.io_repositorysources.yaml \
              -f $RAW/aegisscan.io_scanpolicies.yaml

# 2. Database secret
kubectl create namespace aegisscan
kubectl -n aegisscan create secret generic aegisscan-database \
  --from-literal=connection-string="Host=postgresql;Port=5432;Database=aegisscan;Username=aegisscan;Password=CHANGE_ME"

# 3. Install the chart from GHCR
helm upgrade --install aegisscan oci://ghcr.io/jasonrve/charts/aegisscan \
  --namespace aegisscan \
  --set authentication.authority=https://keycloak.example.com/realms/aegisscan \
  --set ingress.enabled=true --set ingress.className=traefik \
  --set 'ingress.hosts[0].host=aegisscan.example.com' \
  --set 'ingress.hosts[0].paths[0].path=/' --set 'ingress.hosts[0].paths[0].pathType=Prefix'
```

Without an ingress: `kubectl -n aegisscan port-forward svc/aegisscan-aegisscan-web 8080:80`.

Useful values (full list in [`values.yaml`](https://github.com/Jasonrve/AegisScan/blob/main/deploy/helm/aegisscan/values.yaml)):

| Value | Purpose |
|---|---|
| `authentication.authority` | **Required.** OIDC issuer URL the API validates tokens against. |
| `authentication.audience` | Expected `aud` claim (default `aegisscan-web`). |
| `web.oidc.*` | Browser login settings (authority defaults to `authentication.authority`). |
| `gitCache.enabled` | Shared Git mirror on a PVC, see [Git cache](/architecture/git-cache). |
| `serviceMonitor.enabled` | Prometheus scraping. |
| `image.tag`, `web.image.tag` | Pin released versions. |

## Next steps

- [Add your own scanner container](/guide/adding-a-scanner)
- [Configuration](/operations/configuration) and [Identity](/operations/identity)
- [What is missing / roadmap](/roadmap)
