# KubeOps migration

AegisScan now runs its Kubernetes control plane in `src/AegisScan.Operator` using KubeOps 13.2.0 on .NET 11. Custom resources live under `Resources`, reconciliation logic under `Controllers`, and the HTTP compatibility surface under `Api`. KubeOps source generation is enabled in the project file and writes reviewed manifests to `deploy/generated` during a Release build.

The supported production build is the standard KubeOps runtime. NativeAOT is attempted by CI with a trimmed self-contained publish; because KubeOps and the Kubernetes client still use runtime discovery/reflection and the current KubeOps documentation does not promise NativeAOT compatibility, the `aot-probe` workflow is non-blocking and the regular runtime remains the release artifact. The standard published executable was smoke-tested against `/healthz` locally.

## Local commands

```powershell
dotnet tool restore
dotnet restore src/AegisScan.Operator/AegisScan.Operator.csproj
dotnet test tests/AegisScan.Operator.Tests/AegisScan.Operator.Tests.csproj
dotnet build src/AegisScan.Operator/AegisScan.Operator.csproj -c Release
docker compose up --build -d
powershell -ExecutionPolicy Bypass -File .\scripts\compose-e2e.ps1
```

The Compose stack supplies PostgreSQL, k3s, the KubeOps operator/API, and the web UI. The Kubernetes smoke test applies one object of each CRD and waits for status reconciliation.
