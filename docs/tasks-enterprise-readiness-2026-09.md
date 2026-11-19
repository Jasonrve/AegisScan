# AegisScan enterprise-readiness tasks

Source: gap analysis of the current C# port against original architecture docs and frontend expectations (2026-09-06).
Everything is tracked in two passes: **Implemented** (built + builds/runs locally) and **Tested** (verified working, locally via `dotnet test`/`npm test`/manual API checks, with anything needing external services provided by `docker compose`). Tested is only marked once the corresponding Implemented box is checked.

Legend: `[ ]` not started · `[x]` done

---

## P0 — Blocking (security-critical; not safe to run for anyone but the developer today)

### 1. Authentication & authorization (OIDC + server-side RBAC)
- [x] Implemented
- [x] Tested

Real Keycloak instance added to `docker-compose.yml` (pre-provisioned via `deploy/dev/keycloak/realm-aegisscan.json` with admin/operator/viewer test users in matching groups). JWT bearer auth wired in `Program.cs` (`Authentication:Authority`/`Authentication:Audience` config), `groups` claim mapped to ASP.NET Core roles, three policies (`Viewer`/`Operator`/`Admin`) applied per-endpoint in `ApiEndpoints.cs`. Verified against real tokens fetched from Keycloak's password grant: viewer read-only (200/403), operator can run/manage repos but not settings (200/403), admin full access (200), no token → 401, malicious `cloneUrl` scheme → 400.

### 2. Scan execution pipeline
- [x] Implemented
- [x] Tested

`Scanning/ScanExecutionWorker.cs` (a `BackgroundService`) polls for `queued` executions, creates a Kubernetes Job per execution, polls running executions for Job completion, parses results, and finalizes status. Also reconciles executions left `running` by a prior process instance on startup (marks them `failed`). Verified end-to-end against real k3s: created a repository pointing at TruffleHog's own public test-fixtures repo, queued a scan, watched the Job run to completion, and confirmed 3 real findings (AWS/URI/PrivateKey, correct verified flags) came back through `/api/v1/findings`. Also verified the failure path: an unresolvable clone URL fails fast (`backoffLimit: 0`) and the execution is marked `failed` within seconds, not stuck.

### 3. Repository acquisition (git-cache)
- [x] Implemented (scope reduced — see note)
- [x] Tested

**Scope note:** implemented as a direct `git clone --depth 50` per scan Job (via an `alpine/git` init container into an `emptyDir` workspace — `Scanning/ScanJobFactory.cs`), not the full shared bare-mirror-per-repository cache with cross-replica locking that `docs/architecture/git-cache.md` specs. That refinement is what makes multi-replica operation safe and is tracked under item #12 (currently the chart runs 1 replica, so there's no concurrent-access hazard yet). This delivers the actual missing capability — a scanner now has real repository contents to scan — without building a persistent-cache subsystem that only matters once replicas > 1. Verified: the init container successfully cloned a real public repository as part of the end-to-end scan test under item #2.

### 4. Real audit log
- [x] Implemented
- [x] Tested

`AuditEntity` (actor, action, target, outcome, timestamp, JSON metadata) persisted via EF Core; `RecordAuditAsync` in `ApiEndpoints.cs` called from every mutating endpoint (repository create/delete, run queue/cancel, settings update). `GET /api/v1/audit` now reads real rows, newest-first, with `page`/`pageSize` paging, admin-only. Verified: operator creates a repo and admin updates settings, then `GET /audit` as admin shows both events with the correct actor pulled from each JWT's `preferred_username`, correct action/target/metadata, newest-first ordering.

### 5. Input hardening on security-relevant endpoints
- [x] Implemented
- [x] Tested

`CloneURL` scheme allowlist (only `https`/`ssh`, blocks `ext::` and other dangerous transports) added to `POST /api/v1/repositories`; Kestrel `MaxRequestBodySize` capped at 1MB; `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Content-Security-Policy` headers added globally. Verified: `cloneUrl: "ext::sh -c ..."` → 400.

---

## P1 — Core functionality gaps (needed to be a usable product, not just a safe one)

### 6. Scanner definitions CRUD
- [x] Implemented
- [x] Tested

`ScannerEntity` (id, name, image, command/args as JSON arrays with `{{workspace}}` templating, builtIn, enabled) persisted via EF Core, seeded with the same trufflehog/renovate defaults. `POST`/`PUT`/`DELETE /api/v1/scanners` added (admin-only), built-in scanners protected from deletion. `ScanJobFactory`/`ScanExecutionWorker` updated to build the scan container from the looked-up `ScannerEntity` instead of a hardcoded switch, so custom scanners are actually runnable, not just stored. Verified: admin creates/deletes a custom scanner (operator gets 403), built-in delete correctly rejected (400), and a real scan against `trufflehog-fixtures` still completes correctly through the new dynamic lookup path (3 findings, matching the pre-refactor result).

### 7. RepositorySource discovery
- [x] Implemented (GitHub only — see note)
- [x] Tested

`RepositorySourceController` now calls GitHub's public API (`GET /orgs/{org}/repos`), filters by `include`/`exclude` (glob patterns) and `includeArchived`, and creates/updates a real `V1Repository` CRD per match. This closes a deeper gap along the way: CRDs and the Postgres-backed API were two disconnected data models — `RepositoryController` (the `V1Repository` reconciler) now mirrors every reconciled CRD into Postgres (and removes it on delete), so discovered — or directly `kubectl apply`'d — repositories are actually scannable through the same API/scan pipeline, not a second parallel concept.

**Scope note:** only the `github` provider calls a real API; `gitlab`/others report "not implemented yet" in status rather than failing. `credentialSecretRef` (private-org auth) isn't wired up — discovery only works against public orgs today.

Verified against a real org end-to-end via k3s: a `RepositorySource` for `trufflesecurity` with `include: ["test_keys"]` discovered exactly 1 repository, created a `V1Repository` CRD for it, which appeared correctly in `GET /api/v1/repositories` moments later; deleting the CRD removed the Postgres row too.

### 8. Scheduling (cron-driven discovery + scans)
- [x] Implemented
- [x] Tested

`Scanning/SchedulingWorker.cs` (a `BackgroundService`, polls every 30s, cron parsing via `Cronos`): `RepositorySource.schedule` due → bumps an annotation on the CRD, which fires a real reconcile through the existing `RepositorySourceController` (no duplicated discovery logic). `ScanPolicy.schedule`/`fullScanSchedule` due → finds Postgres repositories with matching `policyRef` and queues an incremental/full execution for each directly.

Verified end-to-end via k3s: created a `ScanPolicy` with `schedule: "* * * * *"`, patched a discovered repository's `policyRef` to point at it, and — with no manual API call — watched the scheduler queue a scan within a minute, `ScanExecutionWorker` pick it up, and the execution reach `succeeded`.

### 9. Kubernetes Job execution model
- [x] Implemented
- [x] Tested

Delivered together with #2/#3 — `ScanJobFactory.Build` constructs a `V1Job` (clone init container + scanner container sharing an `emptyDir` workspace, `restartPolicy: Never`, `backoffLimit: 0`, `ttlSecondsAfterFinished: 300` for auto-cleanup). RBAC updated (`deploy/helm/aegisscan/templates/rbac.yaml`): `batch/jobs` (get/list/watch/create/delete), `pods` (get/list), `pods/log` (get). Verified via the same end-to-end test as #2.

### 10. Real observability
- [x] Implemented
- [x] Tested

`prometheus-net.AspNetCore` added: `/metrics` now serves real HTTP metrics (`UseHttpMetrics`/`MapMetrics`) plus custom scan-pipeline metrics (`Scanning/ScanMetrics.cs`): `aegisscan_executions_total{status}`, `aegisscan_findings_total{detector}`, `aegisscan_job_duration_seconds`, `aegisscan_queue_depth{status}`. `aegisscan_up` is now a real gauge, not a hardcoded string. Structured JSON logging via the built-in `AddJsonConsole` (no new logging framework needed), plus correlation-ID middleware (`X-Correlation-Id` request/response header, pushed into the log scope so downstream logs carry it). Verified: `/metrics` shows real Prometheus exposition format; a completed scan populates `aegisscan_executions_total{status="succeeded"}`, three `aegisscan_findings_total` entries by detector, and a non-zero `aegisscan_job_duration_seconds`; log lines are valid JSON with `CorrelationId` in scope.

### 11. Reliability handling
- [x] Implemented
- [x] Tested

- **Retry with jittered exponential backoff**: a failed scan Job is requeued (not immediately dead-lettered) up to `MaxAttempts=3`, with backoff `2^attempt × 10s × jitter(0.8–1.2)` tracked via a new `ExecutionEntity.NotBefore` column that `ProcessQueuedAsync` respects.
- **Dead-lettering**: after exhausting retries, the execution stays `failed` — no further automatic attempts; a human re-queues it via `POST /runs` if warranted.
- **Circuit breaker + retry on external HTTP calls**: the standard .NET resilience handler (`Microsoft.Extensions.Http.Resilience`, one line — `AddStandardResilienceHandler()`) applied to the GitHub-discovery and webhook-notification `HttpClient`s. IdP (Keycloak) calls already get equivalent behavior for free from the JWT middleware's own `ConfigurationManager` (cached JWKS, automatic refresh — matches the "IdP unreachable" runbook's existing assumption).
- Startup orphan-reconciliation (already landed under #2) remains in place.

**Bug found and fixed along the way**: the first retry attempt reused the exact same Kubernetes Job name as the failed attempt, colliding with the not-yet-TTL-cleaned previous Job (`409 Conflict`) and short-circuiting straight to permanent failure without ever really retrying. Job names are now attempt-qualified (`aegisscan-{executionId}-{attempt}`).

Verified against a guaranteed-failing repository end-to-end: watched attempt 1 fail → requeue with ~23s backoff → attempt 2 (distinct Job name, no conflict) fail → requeue with ~43s backoff → attempt 3 fail → correctly dead-lettered as `failed` with attempt count 3 and no further automatic retries.

---

## P2 — Needed to call it "enterprise," not just "working"

### 12. Multi-replica support (leader election / distributed locking)
- [x] Implemented
- [x] Tested

`Scanning/DistributedLock.cs` wraps each `ScanExecutionWorker`/`SchedulingWorker` tick in a Postgres `pg_try_advisory_xact_lock` (transaction-scoped, released automatically on commit/rollback — no held connection to manage). Each worker uses its own lock ID so the two don't contend with each other, only with their own kind across replicas. Helm `replicaCount` default bumped from 1 to 2 now that this is safe (no shared git-cache exists to corrupt — see #3's scope note).

Verified directly: ran a second operator instance against the same Postgres/k3s (bypassing Helm's replica count for a faster local test), queued a scan, and confirmed exactly one Kubernetes Job was created for it — the loser's tick logged an unacquired lock and skipped cleanly, no error, no duplicate. Also caught and fixed a real bug along the way: the advisory-lock SQL needed an explicit `AS "Value"` column alias for EF Core's `SqlQuery<bool>` scalar binding to work at all — without it, both workers threw on every tick and (worse) took the *entire host* down, since `BackgroundServiceExceptionBehavior` defaults to `StopHost`. Also hardened the one startup call that ran outside the retry loop's try/catch, so a future one-off failure there can't repeat that.

### 13. Pagination/filtering/sorting at the API
- [x] Implemented
- [x] Tested

`GET /api/v1/repositories` (`page`/`pageSize`/`status`/`search`), `/runs` (`+repositoryId`/`status`), and `/findings` (`+repositoryId`/`state`/`verified`) now page server-side and return `{items, total, page, pageSize}`; `/audit` (already paginated) updated to the same envelope shape. Verified via direct API calls with each filter combination against real data returning correctly scoped, correctly counted results.

### 14. Rate limiting / API abuse protection
- [x] Implemented
- [x] Tested

Built-in ASP.NET Core rate limiter (`Microsoft.AspNetCore.RateLimiting`, no new package) — a fixed-window limiter (100 requests/minute) keyed per authenticated principal (falls back to remote IP), applied to the whole `/api/v1` group; returns `429` on rejection. Verified by hammering an endpoint 110 times with a valid token: exactly 100 requests returned `200`, the remaining 10 returned `429`.

### 15. Reporting & notifications
- [x] Implemented (CSV only — see note)
- [x] Tested

`GET /api/v1/reports/findings.csv` (real CSV, proper quoting/escaping). `ScanExecutionWorker` POSTs a `scan.completed` JSON payload to the `webhookUrl` setting (already the exact key the `AlertsView` UI writes to) whenever an execution reaches a terminal state — best-effort, failures logged but never affect the execution's own status.

**Scope note:** PDF export is deferred. Every practical option is either a paid/AGPL library (iText7) or carries usage restrictions past a certain company size (QuestPDF's community license) — not something to pull in without the user picking one deliberately. CSV covers the same "exportable findings report" need without that tradeoff.

Verified for real: set `webhookUrl` to a throwaway local HTTP listener on the same Docker network, ran a scan, and confirmed the exact `{event:"scan.completed", executionId, repositoryId, scanner, mode, status}` payload arrived. CSV export returns real, correctly quoted rows for actual findings in the database.

### 16. Real role-mapping backend
- [x] Implemented
- [x] Tested

`RoleMappingEntity` (idpGroup → aegisRole) persisted via EF Core; `GET/PUT/DELETE /api/v1/role-mappings` (admin-only, validated against the known role set). `Api/RoleMappingClaimsTransformation.cs` (`IClaimsTransformation`) runs on every authenticated request: for each IdP group in the token, looks up a mapping and adds the corresponding role claim — additive to, not a replacement for, the existing direct group-name-as-role convention.

Verified against real Keycloak: created a `security-team` group with no built-in meaning and a user in only that group (403 on everything); mapped `security-team → operator` via the API; the *same already-issued* token then passed operator-level checks (200 on read/write, still 403 on admin-only settings) — proving the mapping is applied live per-request, not baked into the token at mint time.

### 17. Backup/DR + retention enforcement
- [x] Implemented
- [x] Tested

`ExecutionEntity.CreatedAt` added; `SchedulingWorker.EnforceRetentionAsync` runs on every locked tick, reading the `retentionDays` setting and bulk-deleting (`ExecuteDeleteAsync`) terminal-status execution rows older than that window. `docs/operations/runbooks.md` gains a real Backup and restore section (`pg_dump`/`pg_restore` procedure, what's stateful vs. reappliable-from-source, how retention fits in) and drops the stale "Git-cache PVC full" runbook entry (no PVC exists anymore per #3's scope note).

Verified: 8 pre-existing execution rows (predating the `CreatedAt` migration, defaulted to a clearly-in-the-past timestamp) were correctly swept on the next tick, with the retention window read from the live `retentionDays` setting (42, from an earlier test) and logged (`"Retention sweep removed 8 execution(s) older than 42 day(s)."`); row count confirmed 0 afterward.

---

## P3 — Polish (do last)

### 18. Remove or replace localStorage-only Users/Profile views
- [x] Implemented
- [x] Tested

Deleted `ProfileView`, `UsersView`, `readLocalUsers`, and the `LocalUser` type from `web/src/main.tsx`; removed "Users" from `adminNav`; removed the dead/conflicting global `.profile` click listener that forced a full-page reload onto the (now-deleted) Profile view, fighting the profile button's own `onClick={() => navigate('Settings')}`. Updated the corresponding unit test (removed) and e2e navigation test (dropped "Users" from the checked view list).

**Related finding surfaced while verifying this in the browser** (not part of #18 itself, flagging for visibility): the web UI has no real OIDC login flow — it never adopted Bearer tokens, still relying on the old role-in-URL-query-param scheme. Now that the backend enforces real auth (#1), every API call from the shipped frontend gets `401`. The backend is correctly secured; the frontend simply can't reach it yet. Implementing a real Authorization Code + PKCE login flow in the SPA is real, scoped work beyond what was in the original 20-item list — flagging it rather than silently doing it.

Verified: `npm run build` and `npm run test` (8/8, was 9 before removing the Users test) pass; loaded the app in a real browser, confirmed no "Users" nav entry, and confirmed clicking the profile button now cleanly navigates to Settings with no reload and no console errors attributable to this change (the pre-existing 401s are the login-flow gap above, not a regression).

### 19. Enforce scan-policy gating settings
- [ ] Implemented
- [ ] Tested

"Block verified findings" / "minimum severity" / "require scan approval" are currently display-only settings with no enforcement in the execution path. Depends on #2/#9.

### 20. CRD upgrade automation & accessibility polish
- [ ] Implemented
- [ ] Tested

---

## Notes

- Explicit non-goals carried over from the architecture decision record — not tracked as gaps: multi-tenancy (ADR 0002: single-team, flat roles by design) and SAML/SCIM (front with an external OIDC gateway instead).
- Already solid, not being re-done: Postgres persistence layer, `/healthz` vs `/readyz` split, RBAC scoped to actual code usage, namespace-aware Helm deployment, CI image signing (Cosign) + SBOM + Trivy/gitleaks on tag release.
