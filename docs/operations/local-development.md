# Local development

See [Getting started (developer)](/development/getting-started) for the full flow. In short:

```bash
docker compose up --build -d                                             # postgres, keycloak, k3s, operator/API, web on :3000
docker compose -f docker-compose.yml -f docker-compose.dev.yml up web   # hot-reload UI on :4173
dotnet test AegisScan.sln
(cd web && npm ci && npm test && npm run build)
```
