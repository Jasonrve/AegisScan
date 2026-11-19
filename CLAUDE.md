# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

See also `AGENTS.md` (layout rules) and `docs/` (VitePress site: `architecture/`, `operations/`, `adr/`).

## Commands

Backend (.NET 10, KubeOps 13.2.0):
- `dotnet test AegisScan.sln` — all tests
- `dotnet test tests/AegisScan.Operator.Tests/AegisScan.Operator.Tests.csproj --filter "FullyQualifiedName~PolicyGatingTests"` — single class/test (use `~TestName` for one test)

Frontend (`cd web`):
- `npm run dev` / `npm run build` / `npm run typecheck`
- `npm run test` (vitest); single file: `npx vitest run src/permissions.test.ts`
- `npm run test:e2e` (Playwright); `npm run test:e2e:compose` runs against the compose stack

Full stack: `docker compose up --build -d` (Postgres, k3s, operator/API, web, Keycloak), then `powershell -ExecutionPolicy Bypass -File .\scripts\compose-e2e.ps1`. `docker compose down -v` deletes local volumes. `docker-compose.dev.yml` adds hot-reload web.

CRD compatibility: `python scripts/check_crd_compat.py` fails if a schema in `deploy/crds/*.yaml` drops fields/enum values or narrows types versus the latest git tag without a new `spec.versions[]` entry. CRDs are checked in, so update them alongside changes to `Resources/`.

## Architecture

One .NET process (`src/AegisScan.Operator`, namespace `AegisScan.KubeOps`) is simultaneously:
1. **Kubernetes operator** — `Resources/` (`V1Repository`, `V1RepositorySource`, `V1ScanPolicy` CRDs, group `aegisscan.io`) reconciled by `Controllers/`.
2. **HTTP API** — `Api/ApiEndpoints.cs` maps `/api/v1/*` (minimal APIs) on PostgreSQL via EF Core `Data/AegisDbContext` (migrations in `Data/Migrations`). Auth is JWT bearer (Keycloak/OIDC); `RoleMappingClaimsTransformation` maps claims to `admin`/`operator`/`viewer` policies (`AegisRoles`). Rate limiting, `/healthz`, `/readyz`, Prometheus `/metrics` are wired in `Program.cs`.
3. **Background scan engine** — `Scanning/`: `SchedulingWorker` queues executions from policies; `ScanExecutionWorker` creates a Kubernetes Job per execution (clone + scanner), polls it, parses output (`RenovateResultParser`, `TruffleHogResultParser`) into Findings, and retries up to 3 attempts. Each worker tick runs under a Postgres advisory lock (`DistributedLock`) so multiple replicas never double-create Jobs; startup reconciles orphaned "running" executions.

Data split: CRDs are the declarative config (repositories, sources, policies); PostgreSQL holds executions, findings, and other runtime state (see `docs/architecture/data-ownership.md`). Scanner plugins only consume workspaces prepared by Aegis (shared git mirror/cache, ADR 0001) and never clone themselves.

Frontend (`web/`, React 19 + Vite + TypeScript, oidc-client-ts): views currently live flat in `web/src` (`main.tsx`, `*View.tsx`, `dataClient.ts` as the sole API layer, `permissions.ts` for role gating). Components never talk to the DB or Kubernetes directly — only through `dataClient`.

Deployment artifacts: `deploy/helm/aegisscan`, `deploy/crds`, `deploy/dev/keycloak`.

The repo root has many planning/report markdown files (`plan*.md`, `tasks.md`, `*-report.md`, etc.); they're working notes, not authoritative docs — prefer `docs/`.
