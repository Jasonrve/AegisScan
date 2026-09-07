# AegisScan agent guide

The repository follows a .NET/KubeOps monorepo shape. Kubernetes entities belong in `src/AegisScan.Operator/Resources`; reconciliation belongs in `src/AegisScan.Operator/Controllers`; HTTP translation belongs in `src/AegisScan.Operator/Api`; the PostgreSQL persistence layer (DbContext + entities) belongs in `src/AegisScan.Operator/Data`; reusable UI primitives belong in `web/src/components`; feature-specific UI belongs in `web/src/features`.

`/api/v1/*` is backed by PostgreSQL via EF Core (`AegisDbContext`), not an in-memory fixture. Do not move database or Kubernetes access into React components. Scanner plugins consume prepared workspaces owned by Aegis; they never clone repositories themselves.

## Commands

- `cd web; npm run dev` — local development
- `cd web; npm run build` — production build
- `dotnet test tests/AegisScan.Operator.Tests/AegisScan.Operator.Tests.csproj` — operator tests
- `cd web; npm run test` — frontend tests
