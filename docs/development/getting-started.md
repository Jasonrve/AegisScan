# Getting started

Run the API with `go run ./cmd/aegisscan` from the repository root. It listens
on `:8080` by default; set `AEGIS_HTTP_ADDR` to change the address.

Run the UI with:

```powershell
cd web
npm install
npm run dev
```

Use `VITE_API_BASE_URL` to enable API-backed repository loading and scan queue
submission. Without it, the UI uses its local dashboard fixtures.

Validation commands are `go test ./...`, `npm run test`, and `npm run build`
from `web`.
