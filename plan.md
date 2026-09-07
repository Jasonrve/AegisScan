# AegisScan — Enterprise Readiness Plan

Prepared 2026-09-05. Grounded in the current tree (not just `production-readiness-report.md` /
`spec-coverage-report.md`, both of which are partially stale — see "What's already changed" below)
plus a live look at the running app on `localhost:4173`.

## Out of scope (another agent is already on these — do not duplicate)

1. Repositories view: org-level discovery with pattern-matched bulk import.
2. Findings view showing no data.
3. TruffleHog demo scan reporting `failed` despite "no verified secrets".
4. "Run Scan" button opening a blank screen.
5. Scans menu "+ Add scanner container" and moving Renovate/TruffleHog settings into per-scanner tabs.

Everything below assumes those land separately. Some items here (persistence, in particular) are
prerequisites those fixes will eventually run into — flag but don't block on them.

## What's already changed since the last audits (don't re-fix)

- Auth now fails closed: `cmd/aegisscan/main.go:56-61` refuses to start without OIDC configured or
  an explicit `AEGIS_ALLOW_ANONYMOUS=true`.
- HTTP server has real timeouts and graceful shutdown (`main.go:63-74`).
- Kubernetes Job execution is wired for real: `internal/operator/kubernetes.go` +
  `internal/server/kubernetes_runs.go` (`operator.NewRemoteKubernetesJobClient`), driven off a
  configurable scanner registry (`internal/domain/scanners.go`).
- Frontend is mid-migration to TypeScript (`web/src/*.ts(x)`, `web/tsconfig.json`) with a much richer
  IA than the old fixture dashboard (Scans submenu, Findings, Alerts, Policies, Settings, plus an
  Administration section: Users, Roles, Audit Logs, System).

## Confirmed still true (verified directly, not just from old reports)

- **`internal/database` (Postgres, migrations, git cache) has zero production importers.**
  `grep -rln 'aegisscan/internal/database"' --include=*.go .` outside `_test.go` returns nothing.
  `docker-compose.yml` sets `AEGIS_DATABASE_URL`, but no Go file reads that variable
  (`grep -rn AEGIS_DATABASE_URL --include=*.go .` only matches the Postgres *test* file's
  `AEGIS_TEST_DATABASE_URL`). Every repository, execution, finding, audit event and scanner
  definition lives in `server.MemoryState` and is lost on every pod restart/redeploy.
- Frontend still pins nothing: `react`, `vite`, `typescript`, `vitest`,
  `@vitejs/plugin-react` are all `"latest"` in `web/package.json`.
- No `SECURITY.md`, no `api/openapi/` directory, no `Makefile`, no `values.schema.json`.
- CI (`.github/workflows/ci.yml`) now runs `go test`, `npm run build`, `npm run test`, `helm lint`,
  and `helm template` — an improvement over the old audit — but still has no lint
  (`golangci-lint`), no `go vet`/`-race`, no `govulncheck`, no container build, no image scan, no
  SBOM, no Playwright.

This plan is organized so an implementing agent can work top to bottom; each phase is
independently shippable and later phases assume earlier ones landed.

---

## Phase 1 — Persistence (the load-bearing gap)

Nothing else in this document matters if the app forgets everything on restart. This is the
single highest-leverage phase.

1. **Wire PostgreSQL into `main.go`.** Open a `pgxpool`/`database/sql` pool from
   `AEGIS_DATABASE_URL`, `PingContext` at startup, fail fast on error, wire it into `/readyz`.
2. **Run migrations at startup**, not by hand. Replace `internal/database/migrate.go`'s single
   `ExecContext` string with a real tool (`golang-migrate` or `goose`, embedded via `embed.FS`), a
   schema-version table, one transaction per migration, and a `pg_advisory_lock` so concurrent pod
   starts (rollout, multi-replica) don't race on DDL.
3. **Swap `server.MemoryState`'s backing store for `internal/database.Postgres`** behind the
   existing `server.API` port interfaces (`Repositories`, `Runs`, `Executions`, `Findings`,
   `Settings`, `Audit`, `Scanners` — these are already narrow interfaces per `ports.go`, so this is
   substitution, not a rewrite). Keep `MemoryState` alive behind a flag for local dev/tests without
   Postgres.
4. **Fix schema correctness before real data lands**: add foreign keys (`ON DELETE CASCADE` for
   executions/findings/checkpoints → `repositories`, `RESTRICT` on the repository itself), add
   indexes for the query patterns the UI actually uses (`scan_executions(repository_id, scanner,
   created_at DESC)`, `scan_executions(status)` partial index for in-flight sweeps,
   `findings(repository_id, last_seen DESC)`, `audit_events(created_at DESC)`), push `LIMIT`/`OFFSET`
   into SQL instead of paginating in Go after loading full tables.
5. **Startup reconciliation**: sweep `scan_executions` rows stuck in `preparing`/`running` on boot
   and mark them `failed` with a `pod_restarted` code, so a rollout doesn't orphan in-flight runs
   forever.
6. **Persist runtime settings and scanner definitions** (currently `MemoryState` only) so an admin's
   configuration survives a restart — `runtime_settings` table already exists in
   `migrations/001_initial.sql`, just needs a real reader/writer.
7. **Connection pool hygiene**: `SetMaxOpenConns`/`SetMaxIdleConns`/`SetConnMaxLifetime` sized for
   the deployment; document the sizing in `docs/operations/configuration.md`.

Acceptance: kill the API pod mid-scan, restart it, and repositories/executions/findings/audit/settings
are all exactly as they were; the stuck execution is marked failed instead of hanging forever.

---

## Phase 2 — Multi-instance correctness

Blocked on Phase 1. Required before `replicaCount > 1` is safe (today it silently corrupts state).

1. **Distributed locking for the Git cache and scheduler dedupe.** Two options, no new
   infrastructure: `pg_advisory_lock` keyed on a hash of the repository ID (simplest, DB is already
   a hard dependency), or a Kubernetes `coordination.k8s.io/Lease` per repository.
2. **Leader election for any singleton loop** (discovery polling, retention/cleanup jobs) via
   client-go's `leaderelection` — only one pod should run these; all pods still serve API traffic.
3. **`ReadWriteMany` git-cache PVC**, configurable via Helm (`gitCache.accessMode`), documented as
   required once `replicaCount > 1`.
4. **Idempotent run submission**: an idempotency key or dedupe on the create-execution endpoint so a
   client retry (or a double-click before Phase-elsewhere fixes the blank-screen bug) can't
   double-queue the same scan.

Acceptance: `helm upgrade --set replicaCount=2` and run a scan — no duplicate Jobs, no corrupted
git-cache mirror, exactly one execution row.

---

## Phase 3 — Security & compliance hardening

Independent of Phases 1–2; can run in parallel.

1. **Credential handling for private repositories.** Nothing today resolves
   `credentialSecretRef` (declared on the `RepositorySource`/`Repository` CRDs, read by no code).
   Add: Kubernetes Secret projection or `secretKeyRef` for Git PATs/SSH keys/GitHub App
   credentials, scoped ServiceAccount `get`-only access to named secrets, and wiring through to the
   clone step. Without this the product only works against public repos.
2. **Validate `CloneURL`** before any `git` invocation: scheme allowlist (`https`, `ssh`), `--`
   separator before the URL argument, `-c protocol.ext.allow=never` in the command environment, and
   a `pattern` constraint on the CRD field. An unvalidated clone URL reaching `git clone` is remote
   code execution in a pod holding the ServiceAccount token and the shared cache PVC.
3. **Shared output sanitizer** applied to every scanner's logs (today only Renovate's is
   sanitized) before persistence and before display in the "View logs" panel, with a length cap.
4. **Complete pod security context**: `allowPrivilegeEscalation: false`,
   `capabilities.drop: [ALL]`, `seccompProfile.type: RuntimeDefault` — currently missing from
   `values.yaml`'s `containerSecurityContext`, which fails the `restricted` Pod Security Standard.
5. **`http.MaxBytesReader`** on all request bodies; enum-validate `scanner`/`mode` instead of
   presence-only checks; `DisallowUnknownFields` on JSON decode.
6. **CSP + HSTS + `Permissions-Policy`** headers now that the UI is actually served
   (`health.go` currently sets `X-Content-Type-Options`/`X-Frame-Options`/`Referrer-Policy` only).
7. **Dependency & supply-chain scanning in CI**: `govulncheck ./...`, `npm audit` (or `osv-scanner`),
   `trivy image` on the built container, `gitleaks` for secret scanning, Dependabot for Go and npm.
8. **Pin frontend dependencies** — replace every `"latest"` in `web/package.json` with exact
   versions; add a lockfile-drift CI check.
9. **Image signing (cosign) + SBOM (syft)** published alongside each release image.
10. **Audit log completeness**: timestamp every event (verify the field survives Phase 1's
    persistence layer), populate `Metadata`, add events for authentication failures and
    authorization denials (today only successful mutations are recorded), add auth/RBAC change
    events once Phase 4's SSO work lands, add a search/filter API + UI beyond simple pagination.
11. **`SECURITY.md`** with a vulnerability disclosure process — conspicuous gap for a product whose
    entire purpose is finding other people's secrets.
12. **Rate limiting** on the API (per-principal, at minimum on run-submission and discovery
    endpoints) to prevent one tenant/token from exhausting scheduler capacity.

Acceptance: a private-repo scan works end to end with a Secret-backed credential; `govulncheck`/
`trivy`/`gitleaks` all run in CI and gate merge; `helm template | kubeconform` passes `restricted` PSA.

---

## Phase 4 — Enterprise identity & access

1. **SSO breadth**: OIDC already works — add SAML 2.0 support (many enterprise IdPs are SAML-only)
   or confirm the target IdPs are OIDC-capable and document that constraint explicitly rather than
   silently.
2. **SCIM provisioning** (or at minimum documented manual user/group lifecycle) so enterprise IT can
   provision/deprovision access without touching the app directly.
3. **Admin UI for role/group mapping.** `JWTVerifier.RoleMappings` exists as a Go struct field with
   no API, no persistence, no UI (`main.go` never populates it) — the Administration → Roles screen
   visible in the running app needs to actually read/write this.
4. **Session/token controls**: configurable token lifetime, a revocation/logout path, and
   (should-have) an IP allowlist option for the API.
5. **Team/namespace-scoped access** if AegisScan will ever serve more than one team from one
   deployment — today RBAC is a single flat Admin/Operator/Viewer matrix with no repository-group
   scoping. Confirm with the business whether multi-team is in scope before building this; if it's
   not, document it as an explicit non-goal (the old ADR pattern in `docs/adr/` is the right place).

Acceptance: an enterprise IdP admin can provision a new Operator user via their existing group
management (SCIM or documented equivalent) without an AegisScan admin touching a config file.

---

## Phase 5 — Observability & operability

1. **Structured logging**: replace `log.Printf`/`log.Fatal` with `log/slog` emitting JSON, with a
   consistent field set (`repository_id`, `execution_id`, `scanner`, `job_name`, `correlation_id`)
   threaded through `ScanService`/`KubernetesRuns`/the scheduler.
2. **Request-ID / correlation-ID middleware**: read `X-Request-Id` or generate one, propagate
   through context and logs, echo it in the response and in error envelopes.
3. **Real Prometheus metrics** via `prometheus/client_golang` — `/metrics` today returns a hardcoded
   `aegisscan_up 1`. Minimum set: executions total/duration by scanner+status, executions in flight,
   git fetch duration, cache hit/miss/size, active findings, discovery runs/errors, HTTP request
   duration by route/method/status.
4. **Real `/readyz`**: `db.PingContext`, cache-root writability, Kubernetes API reachability —
   currently unconditional `200`.
5. **`ServiceMonitor` template** in the Helm chart, gated behind `serviceMonitor.enabled`.
6. **Runbooks** for the failure modes an on-call engineer will actually hit: execution stuck in
   `running`, git-cache PVC full, database unreachable, IdP unreachable, bad-release rollback.
7. **Retention enforcement job** honoring `RuntimeSettings.RetentionDays` (validated and stored
   today, never acted on) — findings/executions/audit tables will otherwise grow forever.
8. **Cache eviction** honoring `CacheRetentionGB` — same problem for the git-cache PVC.
9. **Typed error taxonomy** (`ErrAuthentication`, `ErrTransient`, `ErrConfiguration`,
   `ErrNotFound`, `ErrResourceExhausted`) so the scheduler retries only transient failures with
   jittered exponential backoff instead of retrying every error identically with linear backoff.

Acceptance: a paged engineer can find the failing repository/execution from a log line alone, see a
Grafana panel showing scan duration/error-rate trend, and follow a written runbook for the top five
incidents without guessing.

---

## Phase 6 — API contract & integration surface

1. **OpenAPI 3 spec** under `api/openapi/`, served at `/api/v1/openapi.json`, kept in sync via a CI
   check (generate-and-diff, not hand-maintained drift).
2. **Stable DTOs with JSON tags**, separate from internal domain/DB structs, so the wire format is
   an explicit contract rather than whatever Go's default marshaling happens to produce.
3. **Filtering/sorting query params** on list endpoints (runs, findings) — today only
   `page`/`pageSize` exist.
4. **Webhooks / outbound integrations** for the things enterprise buyers actually ask for: a new
   critical finding → Slack/Teams/PagerDuty, a completed scan → a configurable webhook, findings →
   a ticketing system (Jira) export. Start with one generic outbound webhook; don't build five
   bespoke integrations speculatively.
5. **Scheduled/exportable reports** (CSV/PDF) for compliance audits — "show me every finding closed
   in Q3" is a real enterprise ask, not a nice-to-have for this category of product.

Acceptance: an external system can subscribe to a webhook and receive a real payload on scan
completion; `/api/v1/openapi.json` validates and matches the actual routes.

---

## Phase 7 — Deployment completeness

1. **Ship the UI through Helm.** Today Docker Compose runs a separate `web` + nginx container, but
   the Helm chart deploys the API only — a Helm install gives users no UI. Either add a second
   Deployment/Service/Ingress path for the built frontend (mirroring the Compose nginx pattern) or
   embed the static build into the Go binary (`embed.FS`) and serve it from `internal/server`.
   Pick one; don't build both.
2. **Wire `postgresql.*` values into a template.** `values.yaml` declares
   `postgresql.external/host/port/database/existingSecret` today with no template consuming them —
   dead config. Project `AEGIS_DATABASE_URL` from `existingSecret` via `secretKeyRef`, and support
   both "external Postgres" and "bundled subchart" modes.
3. **Migration Job**: a `helm.sh/hook: pre-upgrade` Job (or rely on Phase 1's startup migration under
   advisory lock — pick one and document it) so schema changes have a real delivery path on upgrade.
4. **`values.schema.json`**, commented `values.yaml`, `NOTES.txt` post-install output,
   `PodDisruptionBudget`, HPA (meaningful once Phase 2 lands), scanner image/version/resource
   defaults (spec'd, still absent), printer columns on the CRDs so `kubectl get repositories` shows
   more than NAME/AGE.
5. **CRD upgrade documentation**: `crds/` in a Helm chart is never upgraded by `helm upgrade` — the
   `kubectl apply -f deploy/crds` step needs to be in the upgrade guide, not just the install guide.

Acceptance: `helm install` on a clean cluster gives a working UI, a working database connection, and
survives a `helm upgrade` without manual intervention beyond the documented CRD step.

---

## Phase 8 — CI/CD

1. `golangci-lint run ./...` (errcheck, gosec, staticcheck, ineffassign).
2. `go test -race ./...` — the scheduler and git cache are concurrent by design and currently
   untested under the race detector.
3. `govulncheck ./...`, `npm audit`/`osv-scanner`, `trivy image` on the built container,
   `gitleaks detect`.
4. Actually build the container (`docker build .`) in CI — it isn't today.
5. Playwright in CI with trace-on-failure artifacts (currently local-only).
6. Publish a multi-arch image to GHCR on tag, cosign-signed, with an SBOM; package and publish the
   Helm chart to an OCI registry (today's `Chart.yaml` version is hardcoded `0.1.0` with no release
   process).
7. `concurrency:` group to cancel superseded runs; Go module caching.
8. Delete the committed `output/helm-*-rendered.yaml` files — they're evidence a human ran
   `helm template` by hand; replace with the CI step that does it automatically.

Acceptance: a PR that introduces a lint violation, a data race, a known CVE, or a leaked secret
fails CI before merge, not after deploy.

---

## Phase 9 — Frontend architecture & UX polish

(Beyond the 5 items already in flight.)

1. **Finish the TypeScript migration** — it's in progress (`web/src/*.ts(x)` alongside the old
   `.js`/`.jsx` files per `git status`); make sure nothing is left dual-tracked.
2. **Accessibility**: focus trap in modals, skip link, WCAG 2.1 AA color contrast audit — enterprise
   procurement (VPAT/ACR) will ask for this explicitly.
3. **Loading/skeleton states** for async views — currently minimal.
4. **Pagination and search/filter/sort in the UI** for Runs and Findings (server already supports
   `page`/`pageSize`; the UI doesn't expose it everywhere).
5. **Notification center**: in-app alerts exist; connect them to the Phase 6 webhook/notification
   settings once those land.

Acceptance: keyboard-only navigation reaches every actionable control; an automated axe scan shows
zero critical violations on the five primary views.

---

## Suggested sequencing for an executing agent

1. Phase 1 (persistence) — everything else compounds on this.
2. Phase 3 items 1–2 (credential handling, CloneURL validation) — security-critical, independent of
   Phase 1, do in parallel.
3. Phase 5 items 1–4 (logging, correlation IDs, metrics, readyz) — cheap, high-value, independent.
4. Phase 8 (CI/CD hardening) — do as soon as Phase 3's scanning tools exist; gates everything after.
5. Phase 2, 4, 6, 7 in any order once Phase 1 is stable.
6. Phase 9 continuously, alongside the UI bugs already being fixed elsewhere.

## Definition of "enterprise ready" for this product

- Survives a pod restart with zero data loss (Phase 1).
- Safe to run more than one replica (Phase 2).
- Passes a third-party security review: no RCE via clone URL, credentials never logged or
  persisted in full, dependency/image/secret scanning gates CI, `SECURITY.md` exists (Phase 3).
- SSO-provisioned access with auditable role changes (Phase 4).
- An on-call engineer can diagnose a production incident from logs/metrics/runbooks alone (Phase 5).
- Has a documented, versioned API and at least one outbound integration path (Phase 6).
- `helm install` produces a complete, working system — UI included — on a clean cluster (Phase 7).
- CI blocks merges that introduce known vulnerabilities, lint violations, or race conditions
  (Phase 8).
- Meets a baseline accessibility bar for procurement (Phase 9).
