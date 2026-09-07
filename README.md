# AegisScan

Kubernetes-native security automation for repository discovery, scanner execution, and findings.

Start with the [developer documentation](docs/development/kubeops-migration.md), follow the [getting started guide](docs/development/getting-started.md), or browse the [VitePress documentation site](docs/).

```powershell
dotnet test AegisScan.sln
docker compose up --build -d
powershell -ExecutionPolicy Bypass -File .\scripts\compose-e2e.ps1
```

The operator targets .NET 10 and KubeOps 13.2.0. The local stack includes PostgreSQL, k3s, the operator/API, and the web console. Use `docker compose down` to stop it; add `-v` only when you intend to remove its local volumes.
