# Local development

The web vertical slice runs with local fixtures so its operator-oriented layout can be developed independently of infrastructure:

```powershell
cd web
npm install
npm run dev
npm run test
npm run build
```

The Go API foundation exposes `/healthz` and `/readyz`:

```powershell
go run ./cmd/aegisscan
Invoke-WebRequest http://localhost:8080/healthz
```

The target architecture keeps Kubernetes resources for desired state, PostgreSQL for executions/findings/checkpoints/audit history, and prepared workspaces for scanner plugins. Scanner plugins must never clone repositories themselves.
