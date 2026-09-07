# Getting started

The repository targets .NET 10 and KubeOps 13.2.0. Install the .NET 10 SDK, Node.js 22, Docker Desktop or Rancher Desktop with a working container engine, and Helm before starting.

Run the KubeOps operator/API with `dotnet run --project src/AegisScan.Operator` from the repository root. It listens on `:8080` by default; set `ASPNETCORE_URLS` to change the address.

Run the UI with:

```powershell
cd web
npm install
npm run dev
```

The UI uses the API by default and the Vite server proxies `/api` to
`http://127.0.0.1:8080`. Set `VITE_API_BASE_URL` only when the API is hosted at
another base URL. Fixture mode is reserved for automated UI tests and must be
explicitly enabled with `VITE_FIXTURE_MODE=true`.

For a live local stack with frontend hot reload, start the infrastructure and
API with `docker compose up -d --build`, then run the Vite override with
`docker compose -f docker-compose.yml -f docker-compose.dev.yml up web`.
Open `http://localhost:4173`; edits under `web/src` reload immediately and API
requests still go through the real .NET operator/API, PostgreSQL, and k3s stack.

Validation commands are `dotnet test tests/AegisScan.Operator.Tests`, `npm run test`, `npm run build` from `web`, and `npm run build` from `docs`.
