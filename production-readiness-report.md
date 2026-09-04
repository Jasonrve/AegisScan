# AegisScan — Production Readiness Report

Assessed independently of the build specification: what would this need to run in a real cluster,
handling real repositories, for a team that gets paged?

## Verdict

**Not production ready. This is a well-structured prototype, not a deployable system.**

The single fact that governs everything below: `internal/database`, `internal/operator`,
`internal/plugins` and `internal/discovery` have **zero production importers**
(`grep -rn 'aegisscan/internal/database"' --include=*.go .` → no hits, same for the other three).
`cmd/aegisscan/main.go:16` constructs `server.NewMemoryState(nil)` and serves it over HTTP. There is
no database connection, no Kubernetes client, no scanner execution and no scheduler in the running
binary.

So "production readiness" splits into two questions, and both matter:

- **A. Is the deployed artifact safe and operable?** No — it is an unauthenticated in-memory API
  with one hardcoded metric, no logging fields, no timeouts and no graceful shutdown.
- **B. Are the unwired subsystems ready to be wired?** Partially. The interfaces and tests are
  genuinely good. Several of the implementations have correctness bugs that unit tests miss
  (see `code-suggested-changes.md` findings 1, 4, 5, 6, 7).

What *is* solid and worth preserving: the port/adapter boundaries (`internal/app/ports.go`),
consistent error wrapping, context propagation throughout, the Git-cache concurrency and
corrupt-recovery tests (`internal/database/gitcache_test.go`), the role/permission matrix
(`internal/domain/authorization.go`), and the discipline of not persisting raw secrets
(`internal/plugins/trufflehog.go:100`).

---

## 1. Observability

**Current state**

| Signal | State |
|---|---|
| Logging | `log.Printf` / `log.Fatal` in `cmd/aegisscan/main.go:22,26,28`. **Nothing under `internal/` logs anything.** No levels, no structure, no fields. |
| Metrics | `/metrics` (`internal/server/health.go:15-18`) returns a hardcoded string: `aegisscan_up 1`. No Prometheus client library in `go.mod`. |
| Tracing | None. |
| Correlation IDs | None anywhere — not generated, not propagated, not logged, not returned. |
| Health | `/healthz` and `/readyz` (`health.go:7,11`) both return unconditional `200`. `/readyz` checks nothing — not the DB, not the cache PVC, not the K8s API. |

**Why this is the highest-priority gap after security:** when a scan fails in production you will
have an `ErrorCode` of `scanner_failed` (`app/scan.go:74`) and literally nothing else. No log line
identifies which repository, which execution, which commit range or which Job.

**Must-have**

- Replace `log` with `log/slog` (stdlib, no dependency). Emit JSON to stdout. Establish the field
  set the spec §18 already names: `repository_id`, `execution_id`, `scanner`, `job_name`,
  `correlation_id`. Thread a `*slog.Logger` through `ScanService` and the adapters.
- Request-ID middleware: read `X-Request-Id` or generate one, put it in the request context,
  include it in every log line and echo it in the response header. Add it to the error envelope so
  a user-reported failure maps to a log search.
- Real `/readyz`: `db.PingContext`, cache-root writability, and (once wired) K8s API reachability.
  A liveness probe that always returns 200 cannot restart a wedged pod.
- Prometheus via `prometheus/client_golang`. Minimum set:
  `aegisscan_executions_total{scanner,status}`, `aegisscan_execution_duration_seconds{scanner,mode}`
  (histogram), `aegisscan_executions_in_flight{scanner}`, `aegisscan_git_fetch_duration_seconds`,
  `aegisscan_git_cache_operations_total{result}` (hit/miss/error), `aegisscan_git_cache_bytes`,
  `aegisscan_findings_active{repository}`, `aegisscan_discovery_runs_total{result}`,
  `aegisscan_http_request_duration_seconds{route,method,status}`.
  Note that `Execution.StartedAt`/`FinishedAt` are never set (`app/scan.go`), so duration metrics
  are blocked on that fix first.

**Should-have**

- OpenTelemetry traces spanning API → scheduler → cache prepare → Job → result parse. Optional
  exporter, off by default (spec §20 rightly says don't require extra infrastructure).
- A `ServiceMonitor` template in the Helm chart, gated on `serviceMonitor.enabled`.
- Structured log sampling for the high-cardinality scanner output path.

**Nice-to-have**

- Grafana dashboard JSON shipped in `deploy/`.
- `pprof` on a separate, non-ingress-exposed port.

---

## 2. Error handling and resilience

**Current state**

Error *wrapping* is consistently good — `fmt.Errorf("...: %w", err)` throughout. Context propagation
is correct. Retry with backoff exists (`internal/app/scheduler.go:73,98`). Timeout exists (`:76`).
Cancellation exists (`:125`).

What is missing:

- **Error classification is two strings.** `repository_prepare_failed` and `scanner_failed`
  (`app/scan.go:59,74`). No distinction between "credentials rejected", "repository not found",
  "network timeout", "disk full", "scanner OOM-killed" — all of which need different operator
  responses and different retry policies.
- **Retries are blind.** `scheduler.go:73` retries every error identically. Retrying an auth failure
  or a malformed-config error is pure waste; retrying a transient network error is correct.
- **Backoff is linear** (`scheduler.go:98`: `Backoff * (attempt+1)`) with no jitter. Under a provider
  outage, all repositories retry in lockstep — a thundering herd against the very service that is
  already down.
- **No circuit breaker** on the Git provider or the IdP. `auth.go:58` fetches JWKS on every single
  request; if the IdP degrades, every API call degrades with it.
- **Partial-failure handling loses work.** `app/scan.go:82-84` returns immediately if one finding
  fails to persist, abandoning the remaining findings *and* leaving the execution row in `running`
  forever. Same at `:96` (checkpoint) and `:103` (full-scan timestamp).
- **No dead-letter / poison-pill handling.** A repository that fails every time will be retried
  forever with no quarantine.

**Must-have**

- A typed error taxonomy in `internal/domain`: `ErrAuthentication`, `ErrNotFound`, `ErrTransient`,
  `ErrConfiguration`, `ErrResourceExhausted`. Adapters classify; the scheduler decides retry from
  the class; the API maps class → HTTP status and a stable `code`.
- Retry only `ErrTransient`. Fail fast on `ErrAuthentication` / `ErrConfiguration`.
- Exponential backoff with full jitter, capped.
- Ensure every early return in `ScanService.Run` writes a terminal execution status. Today six
  return paths (`scan.go:69,83,90,97,104,110`) leave the row in a non-terminal state.
- Startup reconciliation: on boot, sweep `scan_executions` for rows stuck in
  `preparing`/`running` from a previous pod and mark them `failed` with a `pod_restarted` code.
  Without this, a rollout permanently orphans in-flight executions.

**Should-have**

- Circuit breaker around the Git provider API and JWKS.
- Bulkheading: the per-scanner semaphore (`scheduler.go:110`) is the right idea; extend it to a
  per-provider limit so one org's rate limit doesn't starve others.
- Idempotency keys on the run-submission endpoint so a client retry doesn't double-queue.

---

## 3. Security hardening

This is a security product; its own posture is part of the deliverable.

**Current state**

| Control | State |
|---|---|
| Authentication | ⚠️ **Off by default.** `main.go:20` installs auth only when all four `AEGIS_OIDC_*` vars are set. Helm defaults `oidc.enabled: false` (`values.yaml:43`). |
| Anonymous access | ❌ `api.go:76-77` returns `RoleViewer` for callers with no principal — anonymous read of repositories, runs, findings and settings. |
| Authorization | ✅ Enforced server-side per handler; role matrix tested (`domain/authorization_test.go`). Dev role header correctly gated (`security_test.go:11`). |
| Token verification | ✅ Signature + issuer + audience validated (`auth.go:148`). RSA-only JWKS (`:110`) — correctly refuses non-RSA and `alg:none` can't pass because `KeyFunc` returns an `*rsa.PublicKey`. |
| Parameterized SQL | ✅ Every query uses `$n`. |
| Secrets not persisted | ✅ No credential field on any domain type or table. |
| Full secrets not stored | ✅ `trufflehog.go:100` hashes and discards `Raw`. |
| Security headers | ⚠️ `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` (`health.go:47-53`). No CSP, no HSTS, no `Permissions-Policy`. |
| CSRF | N/A today (no cookies, bearer-only) — but must be addressed if cookie sessions are ever added. |
| Input validation | ⚠️ Presence checks only. No enum validation on `scanner`/`mode`, no body size limit, no `DisallowUnknownFields`. |
| Container | ✅ Distroless nonroot, static build, `USER nonroot` (`Dockerfile:11-13`). |
| Pod security context | ⚠️ `runAsNonRoot`, `runAsUser: 65532`, `readOnlyRootFilesystem: true`. Missing `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault` — fails `restricted` PSA. |
| K8s RBAC | ✅ Namespaced Role, reasonably scoped (`rbac.yaml:4-13`). `secrets: [get]` without `list`/`watch` is correct. |
| NetworkPolicy | ⚠️ Present, but egress `to: []` = all destinations on 443/5432/53. |
| Command execution | ❌ `git` invoked with an unvalidated, user-supplied URL — `ext::` transport is RCE. See `code-suggested-changes.md` #2. |
| Dependency scanning | ❌ No `govulncheck`, no Dependabot, no SBOM. |
| Image scanning | ❌ Not in CI. |

**Must-have before any production use**

1. **Fail closed on authentication.** Refuse to start without either OIDC configured or an explicit
   `AEGIS_ALLOW_ANONYMOUS=true` dev flag. Change the no-principal default from `RoleViewer` to
   "no role".
2. **Validate `CloneURL`** — scheme allowlist (`https`, `ssh`), `--` before the URL in every `git`
   invocation, `-c protocol.ext.allow=never` in the command env, and a `pattern` constraint on the
   CRD field. Without this, anyone who can create a `Repository` CR gets RCE in a pod that holds the
   ServiceAccount token and the shared cache PVC.
3. **`http.MaxBytesReader`** on all request bodies; HTTP server timeouts (`ReadHeaderTimeout` etc.).
4. **Complete the pod security context** — `allowPrivilegeEscalation: false`,
   `capabilities: {drop: [ALL]}`, `seccompProfile: {type: RuntimeDefault}`.
5. **Credential handling for Git and the database.** Right now there is no code path that reads a
   credential at all: `discovery.GitHubProvider.Token` (`provider.go:29`) is a struct field nothing
   populates, and no template projects `postgresql.existingSecret` into the pod. Wire Secrets via
   `secretKeyRef` / projected volumes, never via `values.yaml` literals.
6. **A shared output sanitizer applied to every scanner's logs** before persistence, with a length
   cap. Today only Renovate is sanitized, with keyword matching that a single-line JSON log defeats.

**Should-have**

- Rotate/scope the JWKS cache (finding #20) so the IdP is not a per-request dependency.
- `govulncheck` + `trivy image` + `gitleaks` in CI; Dependabot for Go and npm.
- Pin `web/package.json` dependencies — `"react": "latest"`, `"vite": "latest"`,
  `"typescript": "latest"`, `"vitest": "latest"` (`web/package.json:12-19`) make builds
  non-reproducible and are a supply-chain hazard.
- Audit-log authentication failures and authorization denials (currently only successful mutations
  are recorded).
- Sign images (cosign) and publish an SBOM.

**Nice-to-have**

- CSP + HSTS once the UI is actually served.
- mTLS or a service mesh policy between API and PostgreSQL.
- Per-tenant/per-namespace scoping if multi-team use is anticipated (explicitly a non-goal for v1).

---

## 4. Testing

**Current state — the strongest part of the repository.**

| Layer | Files | Assessment |
|---|---|---|
| Domain | `domain/{authorization,checkpoint,policy,settings}_test.go` | ✅ Pure logic well covered |
| App | `app/{scan,scheduler,scan_policy}_test.go` | ✅ Concurrency, retry, timeout, duplicate rejection |
| Git cache | `database/gitcache_test.go` | ✅ Real `git` binary, clone→fetch→release, corrupt recovery, 2-goroutine concurrency |
| Plugins | `plugins/{trufflehog,trufflehog_command,renovate}_test.go` | ⚠️ Fixtures encode the wrong TruffleHog JSON shape (`trufflehog_command_test.go:25`) — the test confirms bug #6 rather than catching it |
| Operator | `operator/{job,executor,repository}_test.go` | ⚠️ Tests pure functions; no controller exists to test |
| Discovery | `discovery/provider_test.go:26` | ❌ Asserts only `Name`, so the `clone_url` decode bug (#4) passes CI |
| Server | 8 test files | ✅ Good: role gating, auth-vs-probe routing, audit, pagination |
| PostgreSQL | `database/postgres_integration_test.go:15` | ❌ Skipped unless `AEGIS_TEST_DATABASE_URL` is set — never runs in CI. Covers checkpoints only; `CreateExecution`, `SaveFinding`, `AppendAudit`, `ListRepositories` are untested against a real database |
| Frontend | 4 files, ~8 assertions | ⚠️ Thin but the role tests are meaningful |
| E2E | `web/e2e/critical-flows.spec.js` | ⚠️ 2 of the spec's 5 flows; both run against fixtures, so they prove nothing about the backend |
| **CI execution** | `.github/workflows/ci.yml` | ❌ Runs `go test ./...` and `npm run build`. **Never runs `npm run test` or Playwright.** |

The deeper issue: tests validate components in isolation, and the components have never been
assembled. There is not one test that exercises API → scheduler → cache → plugin → persistence. All
five of the bugs in `code-suggested-changes.md` S1 would be caught by a single end-to-end test.

**Must-have**

- **Wire the frontend tests into CI** — one line.
- **PostgreSQL integration tests that actually run**, via `testcontainers-go` or a
  `services: postgres:16` block in the workflow. Cover every method on `Postgres`, plus applying
  migrations to an empty database.
- **One vertical integration test**: seed a local bare repo, construct the real `ScanService` with
  the real `GitCache` and a fake `CommandRunner`, run a full then an incremental scan, assert the
  execution rows, findings, checkpoint and — critically — that the second run performs no network
  operation.
- **Fix the two tests that assert too little** (`provider_test.go:26`, `trufflehog_command_test.go:25`)
  and replace the TruffleHog fixture with real `trufflehog git --json` output.

**Should-have**

- Contract tests over the plugin interface, once `Capabilities()` exists.
- Race detector in CI (`go test -race ./...`) — the scheduler and cache are concurrent by design.
- Coverage reporting with a floor on `internal/app` and `internal/domain`.
- The remaining 3 e2e flows from spec §19, run against a real backend (kind + Helm) rather than fixtures.

**Nice-to-have**

- Fuzz the TruffleHog JSONL parser (`plugins.ParseTruffleHog`) — it consumes untrusted scanner output.
- Load test the scheduler at realistic repository counts.
- Upgrade tests: install chart vN, apply data, upgrade to vN+1, assert migrations and data integrity.

---

## 5. CI/CD

**Current state** — `.github/workflows/ci.yml`, 16 lines, one job: checkout, Go 1.24, `go test ./...`,
Node 22, `npm ci`, `npm run build`.

**Missing entirely:** linting, frontend tests, e2e tests, race detector, container build,
image publish, `helm lint` / `helm template`, CRD validation, vulnerability scanning, secret
scanning, SBOM, release automation, branch protection artifacts, version stamping.

Note that `output/helm-default-rendered.yaml`, `helm-production-rendered.yaml` and
`helm-rendered.yaml` are committed to the repo root — Helm rendering was validated manually and the
evidence checked in, rather than automated. Those files should be deleted and replaced with a CI step.

**Must-have**

```yaml
- golangci-lint run ./...          # errcheck, gosec, staticcheck, ineffassign
- go test -race ./...
- govulncheck ./...
- npm run test  (working-directory: web)
- docker build .                   # the Dockerfile is never built in CI today
- helm lint deploy/helm/aegisscan
- helm template deploy/helm/aegisscan --include-crds | kubeconform -strict
```

**Should-have**

- Publish a multi-arch image to GHCR on tag, with cosign signature and SBOM.
- Package and publish the Helm chart to an OCI registry; the chart version is hardcoded `0.1.0`
  (`Chart.yaml:5-6`) with no release process.
- Playwright in CI with trace-on-failure artifacts.
- Deploy-to-kind smoke test: `helm install` → wait for ready → curl `/healthz` and `/readyz`.
- `concurrency:` group to cancel superseded runs; dependency caching for Go.

**Nice-to-have**

- Preview environments per PR.
- Automated changelog from conventional commits.

---

## 6. Deployment: Helm and CRDs

**Current state** — the chart is a competent skeleton: Deployment, Service, ServiceAccount,
Role+RoleBinding, Ingress, PVC, NetworkPolicy, `_helpers.tpl`, probes, OIDC env wiring, and the three
CRDs packaged under `crds/` (byte-identical to `deploy/crds/` — verified).

**Blocking problems**

1. **The UI is not deployed at all.** `Dockerfile:6-7` copies only `internal/` and `cmd/`. There is
   no `npm run build` stage, no `embed.FS`, and no static-file route in `internal/server`. The chart
   deploys an API-only container. Users get no UI from a Helm install.
2. **PostgreSQL is not wired.** `values.yaml:23-28` declares `postgresql.host/port/database/existingSecret`;
   `grep -rn postgres deploy/helm/aegisscan/templates/` returns nothing. No `DATABASE_URL` env, no
   `secretKeyRef`, no bundled subchart, no external-DB path. The application also never reads a DSN.
3. **No migration mechanism.** No `helm.sh/hook: pre-upgrade` Job, and `database.ApplyMigration`
   (`migrate.go:9`) is never called from `main`. Schema changes have no delivery path.
4. **`replicaCount` above 1 silently corrupts behavior.** State is in-process
   (`server.MemoryState`), the Git-cache lock is a `sync.Mutex` (`gitcache.go:18`), the scheduler's
   dedupe map is in-memory (`scheduler.go:21`), and the PVC is `ReadWriteOnce`
   (`git-cache-pvc.yaml:6`, hardcoded). There is no leader election. Two replicas = two divergent
   states and concurrent mutation of the same cache. `values.yaml:5` defaults to 1 and
   `docs/architecture/git-cache.md` documents the limitation honestly — but nothing *enforces* it.
5. **`readOnlyRootFilesystem: true` with no writable volume.** `os.MkdirTemp("")` writes to `/tmp`.
   Add an `emptyDir` at `/tmp`, or better, derive workspaces under the cache PVC.

**Also missing**

- Scanner images/versions, job resources, concurrency defaults, retention defaults — all named in
  spec §21, none present in `values.yaml`.
- `values.yaml` has **zero comments** (§21 asks for well-commented).
- No `values.schema.json`, no `NOTES.txt`, no `PodDisruptionBudget`, no HPA,
  no `serviceAccount.create`/`annotations`/`automountServiceAccountToken`,
  no probe tuning (`initialDelaySeconds`, `failureThreshold`), no `startupProbe`,
  no `priorityClassName`, no chart-level `labels`/`commonLabels`.
- Ingress has no `backend.service.port.number` fallback and no TLS secret template.
- CRDs: `Repository` is missing spec'd `enabled` and `credentialSecretRef` fields;
  `RepositorySource` declares `subresources.status: {}` but defines no status schema;
  no printer columns (`additionalPrinterColumns`) on any CRD, so `kubectl get repositories` shows
  only NAME/AGE; no `config/samples/`. Since `crds/` in a Helm chart is never upgraded by Helm,
  document the `kubectl apply -f deploy/crds` upgrade step (installation.md does mention install,
  not upgrade).

**Must-have**: items 1–5 above, plus scanner image and resource values.
**Should-have**: PDB, `values.schema.json`, commented `values.yaml`, printer columns, NOTES.txt.
**Nice-to-have**: HPA (only meaningful after the state problem is solved), ServiceMonitor, chart tests.

---

## 7. Database and migrations

**Current state**

`migrations/001_initial.sql` — 6 tables (`repositories`, `scan_executions`, `findings`,
`scan_checkpoints`, `audit_events`, `runtime_settings`), 1 index. `internal/database/postgres.go`
implements 9 methods with parameterized SQL. Neither is reachable from the running binary.

**Problems**

| Issue | Detail |
|---|---|
| No migration system | `migrate.go:9` executes one SQL string. No version table, no ordering, no down migrations, no transaction per migration, no advisory lock. Two replicas starting simultaneously race on DDL. Never invoked. |
| No connection management | No `sql.Open` anywhere in production code. No `SetMaxOpenConns` / `SetMaxIdleConns` / `SetConnMaxLifetime`. No connection health check. |
| Unbounded queries | `postgres.go:27,47,73` — `SELECT ... ORDER BY ...` with no `LIMIT`. Pagination happens in Go after loading everything (`api.go:107`). At 10k executions this is an OOM per request. |
| One index | `001_initial.sql:45` on `findings(repository_id, state)`. `scan_executions` has none — but every read orders by `created_at DESC` and will filter by `repository_id` and `scanner`. |
| No foreign keys | `scan_executions.repository_id`, `findings.repository_id`, `scan_checkpoints.repository_id` are bare `TEXT` with no FK to `repositories(id)`. Orphan rows are guaranteed. |
| No `updated_at` triggers | `repositories.updated_at` is only maintained by the explicit `updated_at=now()` in the upsert (`postgres.go:19`); nothing else. |
| Missing tables | `repository_sync_state`, `execution_attempts`, `finding_occurrences`, `role_mappings` (spec §15). `execution_attempts` matters: today retries overwrite one row, so attempt history is lost. |
| No retention enforcement | `RuntimeSettings.RetentionDays` (`domain/settings.go:18`) is validated and stored; no deletion job exists. Tables grow forever. |
| No timezone/precision concerns | `TIMESTAMPTZ` used correctly throughout — ✅. |

**Must-have**

- A real migration tool (`golang-migrate` or `goose`) with `embed.FS`, run at startup inside
  `pg_advisory_lock`, each migration in its own transaction.
- Open the pool in `main`, configure limits, `PingContext` before serving, wire `/readyz` to it.
- Push `LIMIT`/`OFFSET` into SQL; return `{items, page, pageSize, total}`.
- Add indexes: `scan_executions(repository_id, scanner, created_at DESC)`,
  `scan_executions(status)` (partial, for in-flight sweeps), `audit_events(created_at DESC)`,
  `findings(repository_id, last_seen DESC)`.
- Add foreign keys with `ON DELETE CASCADE` (or `RESTRICT` for `repositories`, so a repo can't be
  deleted out from under its history).
- Fix the execution-ID uniqueness bug (`code-suggested-changes.md` #5) before any data is written —
  it is a schema-level correctness problem, not just a code bug.

**Should-have**

- `execution_attempts` table so retry history survives.
- A retention job honoring `RetentionDays`, with a dry-run mode and audit entry.
- `pgx` native pool rather than `database/sql` (pgx is already the driver) for better
  batching and prepared-statement reuse.
- Read-only replica support for dashboard aggregations.

**Nice-to-have**

- Partition `scan_executions` and `audit_events` by month at scale.
- Materialized views for the dashboard time-series charts.

---

## 8. Configuration and secrets

**Current state**

- Process config: five bare `os.Getenv` calls in `cmd/aegisscan/main.go:12,17,19` — `AEGIS_HTTP_ADDR`,
  `AEGIS_ALLOW_ROLE_HEADER`, and four `AEGIS_OIDC_*`. No `internal/config` package (spec §17
  prescribes one; §18 says "no magic environment variables scattered through the code").
- One good validation: `main.go:21` fails fast on partial OIDC config.
- Runtime config: `domain.RuntimeSettings` (`settings.go:13`) is typed, validated (`:28`), has
  provenance (`SettingSource`, `:5-11`) and an API (`api.go:234`). This part is well designed.
- Secrets: **no secret is read anywhere.** `GitHubProvider.Token` (`provider.go:29`) is never
  populated. No DSN. No scanner credentials. `credentialSecretRef` exists in the CRD
  (`aegisscan.io_repositorysources.yaml:27`) and no code reads it.

**Must-have**

- An `internal/config` package: one struct, one `Load()` with defaults and validation, one place
  that touches the environment. Fail fast on invalid combinations at startup.
- Secret loading via projected volumes or `secretKeyRef`, never `values.yaml` literals. Read the DB
  DSN and Git provider tokens from files where possible so rotation doesn't require a restart.
- Resolve `credentialSecretRef` from the Kubernetes API when the operator is built; scope the
  ServiceAccount to `get` on named secrets only (the Role already avoids `list`/`watch` — good).
- Document precedence: Helm bootstrap → runtime override → default. Spec §14 requires this
  explicitly; nothing documents it today.

**Should-have**

- Persist runtime settings (currently `MemoryState.settings`, `state.go:20` — lost on restart,
  despite the `runtime_settings` table existing at `001_initial.sql:66`).
- Surface effective value + source + reset-to-default in the API and UI (spec §14).
- Support external secret operators (ESO / Vault CSI) via annotations rather than integration code.

**Nice-to-have**

- Hot reload of runtime settings without restart (the store is already `sync.RWMutex`-guarded).
- Config diff in the audit log.

---

## 9. Scalability and concurrency

**Current state**

Per-component concurrency control is thoughtfully built:
global semaphore (`scheduler.go:61`), per-scanner semaphore (`:66,110`), duplicate rejection
(`:48`), per-repository cache mutex (`gitcache.go:26`), `sync.RWMutex` on all shared state
(`state.go`, `settings.go`, `checkpoints.go`, `audit.go`).

**But every one of these is process-local, and the architecture is single-instance:**

| Concern | Blocker |
|---|---|
| Horizontal scaling | `MemoryState` holds all API state. Two replicas = two truths. |
| Cache correctness | `sync.Mutex` per repo (`gitcache.go:18`) gives no cross-pod protection. Two pods fetching the same mirror on a shared PVC will corrupt it. Spec §7 explicitly forbids relying on in-memory locks. |
| Duplicate work prevention | `scheduler.cancel` map is in-memory; two pods will both accept the same run. |
| PVC access mode | `ReadWriteOnce` hardcoded (`git-cache-pvc.yaml:6`) — a second replica cannot even mount it on most storage classes. |
| Leader election | None. Spec §25 Phase 6 lists it; not started. |
| Scanner execution | In-process (`app/scan.go:71`), so scanner CPU/memory competes with API serving in the same pod and the same cgroup. Kubernetes Jobs (`internal/operator/`) are the designed answer and are unwired. |

Also: in-process scanning means a single large repository's TruffleHog run can OOM-kill the API pod,
taking down health checks and every in-flight request with it.

**Must-have before scaling past one replica**

- Move all state to PostgreSQL (it is already modeled — this is wiring, not design).
- Distributed locking. Two options already available in the architecture, no new infrastructure:
  Kubernetes `coordination.k8s.io/Lease` per repository, or PostgreSQL `pg_advisory_lock` keyed on a
  hash of the repository ID. The latter is simpler and the DB is already a hard dependency.
- Leader election for the scheduler/reconciler (client-go's `leaderelection` package) so only one
  pod schedules, while all pods serve API traffic.
- Execute scanners as Kubernetes Jobs (`internal/operator/executor.go` + a real `JobClient`), with
  per-Job resource requests/limits, so scanner load is isolated from the control plane.
- Make PVC access mode configurable and document that `ReadWriteMany` is required for multi-replica.

**Should-have**

- Queue durability: today a submitted run lives only in a goroutine. A pod restart loses it. Persist
  a queue (a `scan_executions` row in `queued` status plus a claim column is sufficient — no Kafka).
- Backpressure: reject or shed at the API when the queue exceeds a threshold, rather than unbounded
  goroutine spawn (`scheduler.go:57` spawns one goroutine per submission with no admission limit).
- Cache eviction honoring `CacheRetentionGB` — the PVC will fill silently otherwise.

**Nice-to-have**

- Sharding repositories across scheduler replicas by consistent hash.
- Incremental-scan fast path that skips workspace materialization when the commit range is empty
  (`domain.CommitRange` at `checkpoint.go:26-28` already detects `from == to`, but `ScanService`
  proceeds to prepare a workspace and run the scanner anyway).

---

## 10. Documentation and runbooks

**Current state** — all 10 spec-required documents exist and are well written for their length:
`README.md`, `AGENTS.md`, `docs/architecture/{overview,git-cache,scanner-plugins,data-ownership}.md`,
`docs/development/getting-started.md`, `docs/operations/{installation,configuration,troubleshooting,local-development}.md`,
`docs/adr/0001-shared-git-mirror.md`.

`README.md` and `AGENTS.md` are commendably honest ("first runnable vertical slice",
"data-backed by local fixture objects").

**Problems**

1. **Two documents describe behavior the code does not have.**
   `docs/architecture/git-cache.md` and `docs/adr/0001-shared-git-mirror.md` both state the design
   "prevents duplicate network clones". `gitcache.go:68` clones from the remote URL on every
   preparation. An ADR that misstates the implemented decision is worse than no ADR.
   `docs/architecture/data-ownership.md` says "PostgreSQL owns execution history…" — the process
   never connects to PostgreSQL.
2. **`tasks.md` significantly overstates delivery.** 60+ items marked `[x]`, including
   "Implement scheduling, retries, cancellation, stale-job handling, and Kubernetes Job execution"
   (no Kubernetes execution exists) and "Serve health, metrics, repository, execution, and findings
   routes from the runnable Go entrypoint" (served from in-memory state). Anyone reading `tasks.md`
   as a status report will be badly misled. `task.md` and `tasks.md` also duplicate each other.
3. **No runbooks.** `docs/operations/troubleshooting.md` is 6 bullets. There is no procedure for:
   database failover or restore, cache PVC corruption or exhaustion, stuck executions,
   IdP outage, certificate rotation, rollback of a bad release, or capacity planning.
4. **No upgrade documentation.** Helm never upgrades `crds/`; `installation.md` covers install only.
5. **No API documentation.** No OpenAPI spec (spec §16 requires one), no endpoint reference.
6. **No ADRs for the significant deviations actually taken** — spec §28.4 requires recording
   deliberate deviations. The frontend abandoning MUI/TanStack/ECharts/Router/TypeScript, and the
   Git cache living in `internal/database`, are both substantial undocumented departures.

**Must-have**

- Correct or clearly caveat `git-cache.md`, `adr/0001`, and `data-ownership.md` so they describe the
  code as it is. Add a prominent "Implementation status" section to `README.md` listing which
  subsystems are wired.
- Rewrite `tasks.md` to reflect reality, or delete it and keep `task.md`.
- Runbooks for the five most likely pages: execution stuck in `running`, cache PVC full, database
  unreachable, IdP unreachable, bad-release rollback.
- OpenAPI 3 spec under `api/openapi/`, generated or hand-written, and served at `/api/v1/openapi.json`.

**Should-have**

- ADRs for the frontend stack decision, the in-process-vs-Job execution decision, and the Git cache
  package placement.
- Upgrade guide including the `kubectl apply -f deploy/crds` step.
- Capacity guidance: repositories per replica, cache sizing, DB sizing.

**Nice-to-have**

- Architecture diagrams (the spec's ASCII diagrams could be lifted directly).
- Contribution guide, security policy (`SECURITY.md`) — notable absence for a security product.

---

## Prioritized action list

### Must-have before any production use

| # | Item | Area | Reference |
|---|---|---|---|
| 1 | Wire the backend: DB pool, migrations, `ScanService`, `Scheduler`, `GitCache`, plugins into `main` | All | `cmd/aegisscan/main.go:16` |
| 2 | Fail closed on authentication; no anonymous Viewer | Security | `main.go:20`, `api.go:76` |
| 3 | Validate `CloneURL` (scheme allowlist, `--`, disable `ext::`) | Security | `gitcache.go:52,68` |
| 4 | Clone workspaces from the local mirror, not the network | Correctness | `gitcache.go:68` |
| 5 | Fix GitHub `clone_url`/`default_branch` decoding; set `Repository.ID` | Correctness | `provider.go:64,79` |
| 6 | Unique execution IDs | Correctness | `app/scan.go:36` |
| 7 | Fix TruffleHog `Git` vs `Files` metadata; populate `CommitSHA` | Correctness | `trufflehog.go:29` |
| 8 | Set `StartedAt`/`FinishedAt`; terminal status on every error path | Correctness | `app/scan.go` |
| 9 | Structured logging (`slog`) with repo/execution/scanner/correlation fields | Observability | everywhere |
| 10 | Real Prometheus metrics; real `/readyz` | Observability | `health.go:11,15` |
| 11 | Proper migration system, run at startup under advisory lock | Database | `migrate.go:9` |
| 12 | Push pagination into SQL; add the missing indexes and FKs | Database | `postgres.go:27,47,73` |
| 13 | HTTP timeouts, `MaxBytesReader`, graceful shutdown | Resilience | `main.go:27` |
| 14 | Complete pod security context; wire PostgreSQL secret into Helm | Deployment | `values.yaml:23,31` |
| 15 | Ship the UI (build stage + `embed.FS` + static route) or a separate UI deployment | Deployment | `Dockerfile:6` |
| 16 | Frontend tests + lint + `-race` + `govulncheck` + `helm lint` in CI | CI | `ci.yml:15` |
| 17 | Shared scanner-output sanitizer applied to all plugins, with a length cap | Security | `renovate.go:53` |
| 18 | Correct the three docs that describe unimplemented behavior; fix `tasks.md` | Docs | `docs/architecture/git-cache.md` |

### Should-have soon

- Typed error taxonomy; retry only transient errors; jittered exponential backoff.
- Kubernetes Job execution for scanners (isolates scanner load from the control plane).
- Distributed locking (`pg_advisory_lock` or K8s `Lease`) + leader election; move all state to PostgreSQL.
- Startup sweep of executions orphaned by pod restart.
- PostgreSQL integration tests in CI (testcontainers); one end-to-end vertical test.
- Audit timestamps and metadata; persist audit events and runtime settings.
- JWKS caching with TTL; HTTP client timeouts.
- DTO layer with explicit JSON tags; OpenAPI spec.
- Pin frontend dependency versions; add Dependabot.
- Retention enforcement job; cache eviction honoring `CacheRetentionGB`.
- Scoped NetworkPolicy egress; PDB; commented `values.yaml`; `values.schema.json`.
- Runbooks for the top five failure modes.

### Nice-to-have

- OpenTelemetry tracing; Grafana dashboards; ServiceMonitor.
- Circuit breakers on provider and IdP.
- Partitioned history tables; materialized dashboard views.
- Fuzz testing of the scanner output parsers.
- Image signing and SBOM publication.
- Preview environments; automated changelog.
- The remaining three Playwright flows against a real backend on kind.

---

## Effort characterization

The design work here is largely sound — the interfaces, the layering, the domain model and the test
discipline in `internal/app` and `internal/domain` are what you would want. What is missing is
composition: nothing assembles the pieces, and the pieces that were never assembled have accumulated
bugs that isolated unit tests structurally cannot catch (the `clone_url` decode, the execution-ID
collision, the TruffleHog metadata key, the second network clone — all four pass CI today).

The fastest route to a defensible position is one vertical slice wired end to end — Postgres +
GitCache + TruffleHog + one repository — with an integration test that asserts the second scan does
no network I/O. That single test would have caught four of the five S1 findings, and it forces the
composition work that everything else depends on.
