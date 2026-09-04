# AegisScan

AegisScan is a security and dependency automation control plane. This repository currently contains the first runnable vertical slice: a responsive dashboard prototype that establishes the product's information architecture and interaction language.

## Run the web app

```powershell
cd web
npm install
npm run dev
```

The dashboard is deliberately data-backed by local fixture objects for this first slice. The UI is structured so the API can replace those fixtures without changing the presentation layer.

## Build

```powershell
cd web
npm run build
```

## Run the full local stack with Docker Compose

Rancher Desktop works with the included Compose setup when its container engine is set to Moby. From the repository root:

```powershell
docker compose up --build -d
```

Open the dashboard at [http://localhost:3000](http://localhost:3000). The stack also exposes the API on port 8080 and the bundled k3s Kubernetes API on port 16443. Compose enables demo repositories and the development role header explicitly so the dashboard’s local scan flows can be exercised; these settings are not production defaults.

To run the complete smoke test after the stack starts:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\compose-e2e.ps1
```

Stop the stack with `docker compose down`. Add `-v` only when you intentionally want to remove the local PostgreSQL and k3s volumes.

See `docs/architecture/overview.md` for the intended backend boundaries from the product specification.
