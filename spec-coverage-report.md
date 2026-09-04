# AegisScan — Spec Coverage Report

Audit of the repository at `C:\Users\T14\Documents\ChatGPT\AegisScan` against
`aegis-agent-build-specification.md` (1455 lines). Read-only; no source modified.

## Headline finding

`go build ./...`, `go vet ./...` and `go test ./...` all pass. But **the shipped binary
uses almost none of the code that implements the spec.**

```
$ grep -rn 'aegisscan/internal/database"'   --include=*.go .   → 0 hits
$ grep -rn 'aegisscan/internal/operator"'   --include=*.go .   → 0 hits
$ grep -rn 'aegisscan/internal/plugins"'    --include=*.go .   → 0 hits
$ grep -rn 'aegisscan/internal/discovery"'  --include=*.go .   → 0 hits
```

`internal/database` (Postgres, Git cache, migrations), `internal/operator` (K8s Jobs),
`internal/plugins` (TruffleHog, Renovate) and `internal/discovery` (GitHub) are imported by
**zero production code** — only by their own tests. `cmd/aegisscan/main.go:16` builds a
`server.NewMemoryState(nil)` and serves it. `app.ScanService` and `app.Scheduler` are never
constructed outside `_test.go` files.

The running product is: an in-memory REST API + a 117-line React dashboard of hardcoded
fixtures. Every backend subsystem exists as an isolated, unit-tested library that nothing calls.

---

## Section-by-section

### §1–2 Product vision & design goals

| Item | Status | Evidence |
|---|---|---|
| Operator/control plane | ❌ Missing | No controller-runtime, no client-go, no reconcile loop. `go.mod:5` — sole direct dep is `pgx/v5`. `internal/operator/repository.go:18` is a pure function over structs, not a reconciler. |
| Web/API backend | ⚠️ Partial | `internal/server/api.go` serves 7 routes over in-memory state. |
| React web UI | ⚠️ Partial | Single fixture-driven page, `web/src/main.jsx:1-117`. |
| Shared Git repo cache | 🔍 Stubbed-only | `internal/database/gitcache.go` exists and is tested, but unwired — and it violates the core invariant (below). |
| PostgreSQL | 🔍 Stubbed-only | `internal/database/postgres.go` — never instantiated outside `postgres_integration_test.go:30`. |
| Renovate integration | 🔍 Stubbed-only | `internal/plugins/renovate.go:32` shells `renovate --platform=local --dry-run`. Unwired. |
| TruffleHog integration | 🔍 Stubbed-only | `internal/plugins/trufflehog.go:47`. Unwired, and parses the wrong JSON shape (see §8). |
| Helm chart | ⚠️ Partial | `deploy/helm/aegisscan/` — 7 templates. Gaps in §21. |
| No extra infra introduced | ✅ | Correctly avoided Kafka/Redis/etc. |

**§2.2 "One repository clone, many tools" — ❌ VIOLATED.** This is called out in the spec as
"a fundamental requirement" and restated as "the core invariant". `internal/database/gitcache.go:68`:

```go
run(ctx, workspace, "git", "clone", "--branch", branch, "--reference", canonical, repo.CloneURL, ...)
```

`git clone --reference <mirror> <url>` still contacts `<url>` over the network on every single
workspace preparation. `--reference` only dedupes object *transfer*, not the connection, ref
negotiation or auth. The spec's Flow 3 requirement — "No full network reclone should occur during
the normal path" — is not met. `docs/adr/0001-shared-git-mirror.md` and
`docs/architecture/git-cache.md` both claim the opposite of what the code does.

**§2.3 Incremental by default** — ✅ Logic present: `app/scan_policy.go:12` + `domain/policy.go:5` +
`domain/checkpoint.go:13`, all unit-tested. Never invoked at runtime.

### §4 Data ownership

| Item | Status | Evidence |
|---|---|---|
| CRDs hold bounded desired state | ✅ | `deploy/crds/*.yaml` — status is 5 scalar fields + conditions. No history in status. |
| PostgreSQL holds history | 🔍 Stubbed-only | `migrations/001_initial.sql` defines the tables; the process never opens a DB connection. |
| Secrets never in PG/CRD status | ✅ | No credential columns; `credentialSecretRef` is a name only (`aegisscan.io_repositorysources.yaml:27`). |

### §5 CRDs

| Resource | Status | Notes |
|---|---|---|
| `Repository` | ⚠️ Partial | `deploy/crds/aegisscan.io_repositories.yaml`. Missing spec'd fields `enabled` and `credentialSecretRef`; `scanners` is a flat string array (`:25`) not the per-scanner `{enabled: bool}` object in the spec (§5). No printer columns. |
| `RepositorySource` | ✅ | All spec'd fields present (`aegisscan.io_repositorysources.yaml:20-27`). No `status` schema despite `subresources.status:{}` at `:12`. |
| `ScanPolicy` | ✅ | `aegisscan.io_scanpolicies.yaml:19-26`. Missing per-scanner config blob. |
| Avoided ScanJob/Finding/Plugin CRDs | ✅ | Correctly absent. |
| **Anything reads/writes these CRDs** | ❌ Missing | No Go type, no client, no informer. `pkg/api/v1alpha1/` (spec §17) does not exist. |

### §6 Repository discovery

⚠️ **Partial, and broken.** `internal/discovery/provider.go:33` implements GitHub org enumeration
with Link-header pagination (`:87`), include/exclude glob filters (`:103`) and archived handling (`:75`).

Three defects:
1. **`provider.go:64-67`** — the anonymous decode struct uses `CloneURL`/`DefaultBranch` against
   GitHub's `clone_url`/`default_branch`. Go's JSON matcher is case-insensitive but does **not**
   strip underscores, so both fields decode as `""`. Verified empirically. Every discovered
   repository is unusable (`gitcache.go:36` rejects an empty `CloneURL`).
   `provider_test.go:26` only asserts `got[0].Name`, so the bug ships green.
2. **`provider.go:79`** — `domain.Repository.ID` is never set, so every discovered repo has the
   empty primary key.
3. Nothing calls `Discover`. There is no reconciliation of results into desired state (spec §6.6).

Provider abstraction itself (`Provider` interface at `:23`, domain decoupled from any SDK) — ✅.

### §7 Shared Git cache

| Requirement | Status | Evidence |
|---|---|---|
| One canonical mirror per repo | ✅ | `gitcache.go:42,52` — `git clone --mirror` to `<root>/<id>.git`. |
| Persistent PVC storage | ⚠️ Partial | PVC exists (`git-cache-pvc.yaml`) and mounts at `/var/lib/aegisscan/git-cache` (`deployment.yaml:47`), but no env var tells the app that path, and no code reads it. |
| Incremental `git fetch` on the mirror | ✅ | `gitcache.go:57` — `git remote update --prune`. |
| **No second network clone for workspaces** | ❌ Missing | `gitcache.go:68` re-clones from the remote URL. See §2.2. |
| Per-repository locking | ⚠️ Partial | `gitcache.go:26-33` — process-local `sync.Mutex` only. Spec §7 "Distributed scheduling" explicitly forbids relying on in-memory locks. Honestly documented at `docs/architecture/git-cache.md`. |
| Scanner cannot mutate cache | ✅ | Workspace is a separate clone; `Release` (`:79`) deletes only the temp dir. |
| Workspace cleanup | ✅ | `gitcache.go:75-80`, tested at `gitcache_test.go:39-44`. |
| Configurable cache size/retention | ❌ Missing | `RuntimeSettings.CacheRetentionGB` (`domain/settings.go:19`) is validated and stored; no eviction code exists. |
| Cache health/status visible | ❌ Missing | `Repository.CacheState` is a free-text field never populated by the cache. |
| Corrupt cache recovery | ✅ | `gitcache.go:43-51` (missing `HEAD` ⇒ rebuild), tested `gitcache_test.go:63`. |
| Cache metrics | ❌ Missing | No metrics of any kind (§20). |

### §8 Plugin architecture

⚠️ **Partial.** `app.ScannerPlugin` (`internal/app/ports.go:50`) is `Name()` + `Run()`.
The spec's contract is 5 methods:

| Spec method | Present |
|---|---|
| `Name()` | ✅ `ports.go:51` |
| `Capabilities()` | ❌ Missing entirely — no `Capabilities` type anywhere |
| `ValidateConfig()` | ❌ Missing |
| `BuildExecution()` | ❌ Missing — collapsed into `Run()` |
| `ParseResult()` | ❌ Missing as contract (exists ad-hoc as `plugins.ParseTruffleHog`, `trufflehog.go:75`) |

Without `Capabilities()`, spec §2.3's "the plugin should expose its capabilities" and §8's
"supports incremental / requires writable workspace / produces findings" are unimplementable.
`ScanResult.Output fs.FS` (`ports.go:59`) is declared and never used by any producer or consumer.

Core orchestration is free of scanner-specific branching — ✅ (`app/scan.go` dispatches via
`s.Plugins[scanner]` map only).

**TruffleHog** — 🔍 Stubbed, with a parsing defect:

| Requirement | Status |
|---|---|
| Full scans | ⚠️ `trufflehog.go:47` builds `trufflehog git file://<path> --json` |
| Incremental over commit range | ⚠️ `trufflehog.go:52` — `--since-commit <from> --branch <to>`, but `<to>` is a SHA passed to `--branch`, which expects a branch name |
| Structured result parsing | ❌ **Wrong shape.** `trufflehog.go:29-36` decodes `SourceMetadata.Data.Files.file`. TruffleHog's **git** source emits `SourceMetadata.Data.Git` (`commit`, `file`, `email`, `line`). `Files` is the *filesystem* source. Against the `git` source actually invoked at `:47`, `SafePath` is always `""` and the fingerprint (`:100`) degenerates to `sha256(detector + "\x00" + "" + "\x00" + raw)` |
| Verified/unverified | ✅ `trufflehog.go:31` → `domain.Finding.Verified` |
| Deduplication | ✅ `trufflehog.go:81,102-105` (in-scan) + `state.go:123` / `postgres.go:109` (cross-scan upsert) |
| Finding lifecycle | ✅ `app/scan.go:87-93` + `state.go:133` — full scan resolves unseen fingerprints |
| Timeout & cancellation | ⚠️ `trufflehog.go:84-88` honors `ctx` between lines; `Scheduler.Timeout` (`scheduler.go:77`) exists but is unwired |
| **Never store full secret** | ✅ **Correctly implemented.** `raw.Raw` is consumed only by the SHA-256 at `:100`; it is never assigned to any `domain.Finding` field or logged. |
| Commit reference on findings | ❌ `domain.Finding.CommitSHA` exists (`finding.go:19`) but `ParseTruffleHog` never sets it (`:107`) — a consequence of the `Git` vs `Files` bug above |

**Renovate** — 🔍 Stubbed. `renovate.go:32` invokes the real engine (✅ not reimplemented), counts
`type:update`/`type:error` log lines (`:37-49`), and sanitizes output (`:53`). Uses the prepared
workspace (✅ §8). No PR summary beyond two integers; no per-dependency detail (spec asks for
"PR/update summary information").

### §9 Scheduling and execution

| Requirement | Status | Evidence |
|---|---|---|
| Global concurrency | ✅ | `scheduler.go:61` semaphore |
| Scanner-specific concurrency | ✅ | `scheduler.go:66-72,110-123` |
| Retry with backoff | ✅ | `scheduler.go:73,98` (linear, not exponential) |
| Timeout | ✅ | `scheduler.go:76-80` |
| Cancellation | ✅ | `scheduler.go:125` |
| Duplicate-work prevention | ✅ | `scheduler.go:48-52`, keyed `repo/scanner` |
| Stale job detection | ⚠️ | `scheduler.go:135` sweeps in-process only; never entries from `started` (see code report) |
| Repository mutual exclusion | ⚠️ | Implied by the `repo/scanner` dedupe key; two *different* scanners on one repo still run concurrently |
| **Kubernetes Job execution** | ❌ Missing | `app/scan.go:71` calls `plugin.Run(ctx, ...)` **in-process**. `internal/operator/executor.go` + `job.go` build Job manifests but `JobClient` (`executor.go:14`) has no implementation and `Executor` is constructed only in `executor_test.go:27` |
| K8s Job cleanup/retention | 🔍 | `job.go:64` sets `ttlSecondsAfterFinished: 86400`; `executor.go:43` CleanupStale — both unwired |
| Discovery / per-policy / per-scanner schedules | ❌ Missing | No cron parser, no ticker, no scheduling loop anywhere. `ScanPolicy.schedule` and `fullScanSchedule` (CRD `:22-23`) and `RuntimeSettings.FullScanSchedule` (`settings.go:17`) are stored strings that nothing parses |
| 8 distinct UI statuses | ⚠️ | All 8 defined (`domain/repository.go:45-54`); `queued`/`canceled` set only by `MemoryState`, `skipped` never set by anything |
| Failed scan ≠ scan-with-findings | ✅ | Modeled separately: `Execution.Status` + `ErrorCode` vs `Finding` rows |

### §10 AuthN / AuthZ

| Requirement | Status | Evidence |
|---|---|---|
| OIDC/OAuth2 | ✅ | `server/auth.go` — JWKS fetch (`:83`), RSA key construction (`:125`), issuer+audience+signature validation (`:148`) |
| No local password system | ✅ | None present |
| Admin/Operator/Viewer roles | ✅ | `domain/authorization.go:21-32`, tested |
| Group/claim → role mapping | ✅ | `auth.go:176-204` handles both string and array claims |
| **Enforced server-side** | ⚠️ Partial | Enforced *when configured*. `main.go:20` only installs auth if OIDC env vars are set; otherwise `NewHandler` (`health.go:22`) serves the API with **no authentication**, and `api.go:76-77` defaults every anonymous caller to `RoleViewer` — i.e. the default deployment grants unauthenticated read access to repositories, runs, findings and settings. The Helm chart defaults to `oidc.enabled: false` (`values.yaml:43`) |
| Dev role header gated | ✅ | `api.go:60-66,75-77`, tested `security_test.go:11` |
| Manage role mappings (Admin) | ❌ Missing | `JWTVerifier.RoleMappings` is a struct field with no API, no persistence, no UI, and `main.go:24` never populates it |

### §11 Audit logging

⚠️ **Partial.** Recorded for manual scan (`api.go:166`), cancel (`:193`), settings update (`:260`).
Read endpoint at `api.go:278`, admin-gated.

Missing vs the spec's required capture set:
- **No timestamp.** `domain.AuditEvent` (`authorization.go:34-41`) has Actor/Role/Action/Target/
  Outcome/Metadata — no time field. The DB column exists (`001_initial.sql:63`) but the API cannot
  return it. An audit log without times is not an audit log.
- **`Metadata` is never populated** — `api.go:275` constructs the event without it.
- Not persisted (`MemoryState.audit` at `state.go:19` — lost on restart).
- No policy-change, repository-source-change or RBAC-change events (those features don't exist).
- No search/filter UI (spec §11 final line); no filtering on the endpoint at all.

### §12–13 UI/UX and information architecture

❌ **The prescribed frontend stack is essentially absent.**

| Spec requirement | Actual |
|---|---|
| React 19 | ⚠️ `react: "latest"` unpinned (`web/package.json:14`) |
| **TypeScript** | ❌ `typescript` is a dependency but there is not one `.ts`/`.tsx` file in `web/` |
| Vite | ✅ `web/vite.config.js` |
| **Material UI** | ❌ Not installed. 16.6 KB of hand-rolled CSS (`web/src/styles.css`) |
| **TanStack Query** | ❌ Not installed. `web/src/dataClient.js:1` is a raw `fetch` — no caching, retries or invalidation |
| **TanStack Table** | ❌ Not installed. Tables are `<div>` grids (`main.jsx:109`) |
| **Apache ECharts** | ❌ Not installed. "Charts" are hardcoded SVG `d`/`points` attribute strings (`main.jsx:15-19`, `:108`) that never change with data |
| **React Router** | ❌ Not installed. Navigation is `useState('Dashboard')` (`main.jsx:59`) |
| **React Hook Form + Zod** | ❌ Not installed. The one form is uncontrolled `<select>`s (`main.jsx:113`) |

Structure rules from §17 (`features/`, `components/charts|data-display|feedback|forms|layout`,
`hooks/`, `lib/`, `routes/`, `services/`, `types/`) — ❌ none exist. `web/src/` is 6 flat files;
the entire app is one 117-line `main.jsx`.

UX requirements:

| Item | Status |
|---|---|
| Dark + light mode | ✅ `main.jsx:64,94,102` |
| Responsive | ✅ CSS grid/flex |
| Status chips | ✅ `main.jsx:109` `.pill` classes |
| URL-addressable filters | ⚠️ Only `?repository=` (`main.jsx:60,72`); `?role=` is a client-side trust bug |
| Confirm destructive/expensive ops | ✅ `main.jsx:81` `window.confirm` for Full scans |
| Keyboard/a11y | ⚠️ `aria-label`s and Escape-to-close (`:75`) present; no focus trap in the modal, no skip link |
| Skeleton/loading states | ❌ None |
| Empty states | ⚠️ One (`main.jsx:109` "No repositories match this filter") |
| Actionable error states | ⚠️ One toast (`main.jsx:105`) |
| Pagination | ❌ Not in the UI (server supports `page`/`pageSize`) |
| Search/filter/sort on runs & findings | ❌ Only repository name search |

Information architecture — Dashboard exists visually but every number is a literal
(`main.jsx:15-19,26`). All 6 required dashboard charts (§13) are ❌ **decorative static SVG**.
Repositories / Runs / Findings / Settings / Audit views: `main.jsx:50-55` renders placeholder
tables from the same fixtures — no detail pages, no execution timeline, no commit range, no K8s
Job info, no sanitized logs, no retry/cancel controls.

### §14 Helm vs UI configuration model

⚠️ Partial. `SettingSource` (`domain/settings.go:5-11`) models `default|bootstrap|runtime` and
`SettingsStore.Update` (`app/settings.go:27`) stamps provenance — the right idea. But: no bootstrap
values are ever read from Helm into the app, no reset-to-default, and the UI never displays
effective value or source. Precedence is not documented anywhere.

### §15 PostgreSQL schema and migrations

| Requirement | Status | Evidence |
|---|---|---|
| PostgreSQL from the beginning | ❌ | The process never connects to one |
| Migration system | ❌ | `internal/database/migrate.go:9` is `db.ExecContext(one string)`. No version table, no ordering, no down-migrations, no advisory lock, not called from `main` |
| Tables | ⚠️ 6 of 9 | Present: `repositories`, `scan_executions`, `findings`, `scan_checkpoints`, `audit_events`, `runtime_settings`. Missing: `repository_sync_state`, `execution_attempts`, `finding_occurrences`, `role_mappings` |
| Data access behind interfaces | ✅ | `internal/app/ports.go:10-36` — narrow, capability-oriented |
| Parameterized SQL | ✅ | Every query in `postgres.go` uses `$n` placeholders |
| Indexable + paginated dashboard queries | ❌ | Only one index (`001_initial.sql:45`). `ListExecutions`/`ListFindings`/`ListRepositories` (`postgres.go:47,73,27`) are unbounded `SELECT *` with no `LIMIT`/`OFFSET`; pagination is applied in Go after loading every row (`api.go:107`) |

### §16 API design

| Requirement | Status |
|---|---|
| `/api/v1/...` versioned | ✅ `api.go:53-59` |
| REST | ✅ |
| Consistent error format | ⚠️ `{"code": "..."}` everywhere in `api.go`, but `auth.go:228,233` returns `http.Error` plain text — two formats |
| Pagination | ⚠️ Offset params work (`api.go:93-118`) but responses are bare arrays with no total/next; the client cannot page |
| Filtering / sorting | ❌ No query params on any endpoint |
| Request validation | ⚠️ Presence checks only (`api.go:157`); no scanner/mode enum validation, no body size limit |
| **OpenAPI specification** | ❌ Missing — no `api/openapi/` directory |
| Authorization middleware | ✅ `auth.go:220` |
| Correlation/request IDs | ❌ Missing entirely |
| No secret leakage | ✅ Verified — no credential field is ever serialized |
| **Stable DTOs separate from DB models** | ❌ Domain structs are serialized directly. `domain.Repository`, `Execution`, `Finding`, `AuditEvent` have **no JSON tags**, so the wire format is Go PascalCase (`{"ID":…,"ActiveFindingCount":…}`) while `web/src/dataClient.js:6` reads `repository.activeFindingCount` — always `undefined` ⇒ always renders `0` |
| UI does not touch PG/K8s directly | ✅ |

### §17 Repository structure

| Prescribed | Present |
|---|---|
| `api/openapi/` | ❌ |
| `cmd/aegis*/main.go` | ✅ |
| `config/{crd,rbac,manager,samples}/` | ❌ (CRDs live in `deploy/crds/` instead — acceptable relocation, but no `samples/`) |
| `deploy/helm/` | ✅ |
| `docs/{architecture,development,operations,adr}/` | ✅ |
| `internal/app/{commands,queries}/` | ⚠️ flat |
| `internal/auth/`, `internal/audit/` | ❌ (folded into `server`/`domain`) |
| `internal/cache/{git,locking,workspace}/` | ❌ Git cache lives in `internal/database/gitcache.go` — a genuine layering error: a filesystem/Git adapter in the DB package |
| `internal/config/` | ❌ — `os.Getenv` scattered in `main.go:12,17,19` (spec §18 forbids this) |
| `internal/database/{migrations,postgres}/` | ⚠️ flat; migrations at repo root |
| `internal/discovery/providers/github/` | ⚠️ flat |
| `internal/domain/{repository,execution,finding,policy}/` | ⚠️ flat |
| `internal/operator/{controllers,scheduler}/` | ⚠️ flat; scheduler is in `internal/app` |
| `internal/plugins/` | ✅ |
| `internal/server/{handlers,middleware,dto}/` | ⚠️ flat; **no `dto`** |
| `internal/telemetry/` | ❌ |
| `pkg/api/v1alpha1/` | ❌ — no Go CRD types |
| `web/src/{app,components,features,hooks,lib,routes,services,types}/` | ❌ none |
| `tests/{integration,e2e}/` | ❌ (Playwright lives at `web/e2e/`) |
| `Makefile` | ❌ |

### §18 Engineering principles

| Principle | Status |
|---|---|
| Separation of concerns | ⚠️ Mostly good; Git cache misplaced in `database` |
| Dependency inversion | ✅ Genuinely well done — `app/ports.go`, `discovery.Provider`, `operator.JobClient`, `plugins.CommandRunner` |
| Small interfaces | ✅ |
| Explicitness over abstraction | ✅ |
| Error wrapping with context | ✅ Consistent `fmt.Errorf("…: %w", err)` |
| Error classification | ⚠️ Two codes only: `repository_prepare_failed`, `scanner_failed` (`scan.go:59,74`) |
| Idempotency | ⚠️ `scheduler.go:48` dedupes; but `scan.go:36` derives a **non-unique** execution ID (`repo-scanner`), so re-running collides on the `scan_executions` primary key |
| Context propagation | ✅ Throughout |
| Typed config + startup validation | ⚠️ `RuntimeSettings` is typed and validated (`domain/settings.go:28`); process config is raw `os.Getenv` with one fail-fast check (`main.go:21`) |
| **Structured logging** | ❌ `log.Printf`/`log.Fatal` only (`main.go:22,26,28`). No repository ID, execution ID, scanner, Job or correlation ID in any log line. Nothing in `internal/` logs at all |
| Security by default | ⚠️ See production-readiness report |

### §19 Testing

| Requirement | Status | Evidence |
|---|---|---|
| Domain/app unit tests | ✅ | `app/scan_test.go`, `scheduler_test.go`, `scan_policy_test.go`, `domain/*_test.go` |
| Controller reconciliation tests | ⚠️ | `operator/repository_test.go` tests a pure function — there is no controller |
| Plugin contract tests | ⚠️ | Per-plugin tests exist; no shared contract suite (no `Capabilities` to test) |
| **Git cache concurrency tests** | ✅ | `gitcache_test.go:63` — 2 goroutines, corrupt-cache recovery. Spec's mandatory list is largely met except "incremental commit range determination" (covered separately in `domain/checkpoint_test.go`) |
| PostgreSQL integration tests | ⚠️ | One test, checkpoints only (`postgres_integration_test.go:12`), skipped unless `AEGIS_TEST_DATABASE_URL` is set — so it never runs in CI |
| Scheduler/idempotency tests | ✅ | `scheduler_test.go` |
| Vitest + RTL | ✅ | 4 test files, 8 assertions total (`web/src/*.test.*`) |
| Frontend permission/role tests | ✅ | `permissions.test.js`, `main.test.jsx` |
| Playwright — 5 critical workflows | ⚠️ 2 of 5 | `web/e2e/critical-flows.spec.js`: viewer restriction ✅, operator full-scan ✅. Missing: admin changes runtime settings, discovery→execution→result, failed-execution vs finding |
| **Tests run in CI** | ❌ | `.github/workflows/ci.yml:15` runs `npm run build` — **never `npm run test`**, never Playwright |

### §20 Observability

| Metric family | Status |
|---|---|
| `/healthz`, `/readyz` | ✅ `health.go:7,11` — but both are unconditional `200`; `/readyz` checks nothing |
| `/metrics` endpoint | ⚠️ Exists (`health.go:15`) and returns one hardcoded gauge `aegisscan_up 1` |
| repository count, discovery runs/errors, queued/running executions, execution totals & duration, scan failures, Git fetch duration/errors, cache hit/miss, cache size, active findings, API request duration/errors | ❌ **All 11 missing.** No Prometheus client library in `go.mod` |
| Historical graphs from PG, not Prometheus | ❌ No aggregation endpoints exist |

### §21 Helm chart

✅ Present: image/tag/pullPolicy, replicaCount, imagePullSecrets, service, ingress, RBAC Role+
Binding, ServiceAccount, pod+container securityContext, nodeSelector, tolerations, affinity,
topologySpread, git-cache PVC (size/storageClass), probes, NetworkPolicy, OIDC env wiring.

❌ Missing / broken:

- **`postgresql.*` values (`values.yaml:23-28`) are referenced by no template** — dead config.
  No `DATABASE_URL`, no secret projection, no bundled or external PG wiring reaches the pod.
- No scanner images/versions, no job resources, no concurrency defaults, no retention defaults
  (all four explicitly required by §21).
- PVC accessMode hardcoded `ReadWriteOnce` (`git-cache-pvc.yaml:6`) — not configurable, and
  incompatible with `replicaCount > 1`.
- `values.yaml` has **zero comments** — §21 requires "a well-commented values.yaml".
- No `values.schema.json`, no `NOTES.txt`, no migration hook Job, no PDB, no
  `serviceAccount.create/annotations`, no probe tuning.
- `readOnlyRootFilesystem: true` (`values.yaml:34`) with no writable `emptyDir` — the Git cache's
  `os.MkdirTemp("")` (`gitcache.go:60`) writes to `/tmp` and would fail at runtime.
- `containerSecurityContext` omits `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`,
  `seccompProfile`.

### §22 Deployment model

⚠️ **The UI is not deployed.** `Dockerfile:6-7` copies only `internal/` and `cmd/`; there is no
`npm run build` stage, no static asset embedding, and no file-serving route in `internal/server`.
The Helm chart deploys an API-only container. Spec §22's preferred model ("serving the compiled
React application from the Go API") is not implemented, and no separate UI deployment exists either.

### §23 MVP user flows

| Flow | Status |
|---|---|
| 1 — Repository discovery → UI | ❌ Nothing calls `Discover`; and it decodes `clone_url` incorrectly |
| 2 — First TruffleHog scan | ❌ No K8s Job path; `ScanService` unwired; parser reads the wrong metadata key |
| 3 — Incremental scan, no reclone | ❌ Logic exists and is tested, but unwired — and the workspace path re-clones over the network |
| 4 — Full rescan with confirmation | ⚠️ UI confirmation ✅ (`main.jsx:81`); the POST only appends a `queued` row (`state.go:41-56`) — nothing executes |
| 5 — Renovate | ❌ Unwired |
| 6 — Failure ≠ finding | ⚠️ Correctly modeled in the domain; not observable, since nothing runs |

### §24 Documentation

✅ All 10 required files exist. ⚠️ But `docs/architecture/git-cache.md` and
`docs/adr/0001-shared-git-mirror.md` describe behavior the code does not have ("prevents duplicate
network clones"), and `docs/architecture/data-ownership.md` states PostgreSQL owns history while
the process never connects to it. `README.md` and `AGENTS.md` are honest ("first runnable vertical
slice", "local fixture data") — but `tasks.md` marks 60+ items `[x]` including
"Implement scheduling, retries, cancellation, stale-job handling, and **Kubernetes Job execution**"
and "Serve health, metrics, repository, execution, and findings routes from the runnable Go
entrypoint", which overstates delivery.

### §26 Definition of Done

| Criterion | Met |
|---|---|
| Installs via Helm | ⚠️ Installs; deploys an API with no DB, no UI, no scanning |
| GitHub repos discovered automatically | ❌ |
| Not repeatedly network-cloned | ❌ |
| Cache survives pod restarts | ⚠️ PVC exists; unused |
| Normal updates use incremental fetch | ⚠️ Mirror update ✅; workspace path ❌ |
| TruffleHog full + incremental | ❌ |
| Renovate orchestrated | ❌ |
| Executions Kubernetes-native | ❌ |
| History survives Job deletion | ❌ |
| Findings survive pod restarts | ❌ (in-memory) |
| PostgreSQL is the query store | ❌ |
| CRDs bounded | ✅ |
| RBAC enforced server-side | ⚠️ Only when OIDC configured; default is open |
| Admins configure runtime behavior in UI | ⚠️ API ✅; UI ❌ |
| Operators trigger full/incremental | ⚠️ Queues a row only |
| Viewers read-only | ✅ |
| Dashboard shows what ran/succeeded/failed | ❌ Static fixtures |
| Rich charts | ❌ |
| Failures distinguishable from findings | ✅ (model) |
| Audit events recorded | ⚠️ No timestamps, not persisted |
| No complete secret persisted/exposed | ✅ |
| Helm exposes required config | ⚠️ |
| Prescribed architectural boundaries | ⚠️ |
| Tests cover scheduling/cache/authz | ✅ (the best-covered part of the repo) |
| Docs current | ⚠️ Partly contradicted by code |

**Score: 4 of 25 fully met.**

---

## Summary table

| # | Spec section | Status |
|---|---|---|
| 1–2 | Vision & design goals | ⚠️ Partial (core invariant ❌ violated) |
| 4 | Data ownership | 🔍 Stubbed |
| 5 | CRDs | ⚠️ YAML ✅, no controller |
| 6 | Repository discovery | ⚠️ Partial + decode bug |
| 7 | Shared Git cache | ⚠️ Partial, unwired, reclones |
| 8 | Plugin architecture | ⚠️ 2 of 5 contract methods |
| 9 | Scheduling & execution | ⚠️ In-process only, no K8s Jobs, no cron |
| 10 | AuthN / AuthZ | ⚠️ Good when enabled; open by default |
| 11 | Audit logging | ⚠️ No timestamp, not persisted |
| 12–13 | UI/UX & IA | ❌ Prescribed stack absent, fixtures only |
| 14 | Helm vs UI config | ⚠️ Model present, not wired |
| 15 | PostgreSQL & migrations | 🔍 Stubbed; migration runner inadequate |
| 16 | API design | ⚠️ No OpenAPI, no DTOs, no correlation IDs |
| 17 | Repository structure | ⚠️ ~50% |
| 18 | Engineering principles | ⚠️ Good DI; no structured logging |
| 19 | Testing | ⚠️ Decent unit tests; frontend tests not in CI |
| 20 | Observability | ❌ 1 hardcoded metric |
| 21 | Helm chart | ⚠️ Solid skeleton, PG config dead |
| 22 | Deployment model | ⚠️ UI not shipped |
| 23 | MVP flows | ❌ 0 of 6 working |
| 24 | Documentation | ⚠️ Complete but partly inaccurate |
| 26 | Definition of Done | ❌ 4 of 25 |

## Most significant gaps

1. **Nothing is wired.** `database`, `operator`, `plugins`, `discovery` have zero production
   importers. The binary is an in-memory API. — `cmd/aegisscan/main.go:16`
2. **The core invariant is violated.** Workspace preparation re-clones from the network on every
   scan. — `internal/database/gitcache.go:68`
3. **The frontend does not use the prescribed stack.** No TypeScript, MUI, TanStack, ECharts,
   Router, RHF or Zod; charts are hardcoded SVG strings. — `web/package.json`, `web/src/main.jsx`
4. **No Kubernetes runtime.** No client-go, no controller-runtime, no reconciler, no Job client,
   no scheduling loop. Every CRD is inert YAML.
5. **Discovery silently produces empty clone URLs.** Go JSON does not match `clone_url` to
   `CloneURL`. — `internal/discovery/provider.go:64-67`
6. **The default deployment is unauthenticated.** No OIDC env ⇒ no auth middleware ⇒ every
   anonymous request is a Viewer. — `cmd/aegisscan/main.go:20`, `internal/server/api.go:76`
7. **No observability.** One hardcoded metric, no structured logging, no correlation IDs.
8. **No OpenAPI spec, no DTO layer** — domain structs without JSON tags are the wire contract,
   and the frontend already reads field names the server never emits.
