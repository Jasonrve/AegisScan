# AegisScan implementation tasks

This checklist separates the user's requested product behavior from the attached build specification. The supplied PNG is a visual reference; the Markdown specification is the product/engineering source of truth.

## Phase 0 — Foundation

- [x] Create the prescribed monorepo structure and implementation plan.
- [x] Add the React/Vite web application shell, scripts, and baseline documentation.
- [x] Add backend module boundaries for domain, app, adapters, server, and operator packages.
- [x] Add Helm chart skeleton, PostgreSQL migration layout, and CI checks.
- [x] Add runnable Go API entrypoint, non-root container definition, and local operations documentation.

## Phase 1 — Repository management

- [x] Define Repository, RepositorySource, and ScanPolicy desired-state resources.
- [x] Implement repository discovery provider boundaries and paginated GitHub discovery flow.
- [x] Add thin repository reconciliation/status logic with Ready, Error, and Disabled outcomes.
- [x] Implement the shared persistent Git cache with per-repository locking and isolated workspaces.
- [x] Add cache concurrency, corrupt-cache recovery, cleanup, and incremental-fetch tests.
- [x] Connect repository API/UI flows to a concurrency-safe server state plane.
- [x] Add a dependency-free frontend API client that prefers repository DTOs and safely falls back to local fixtures.
- [x] Add thin role-aware HTTP DTO routes for repository reads and manual run requests.

## Phase 2 — Execution framework and TruffleHog

- [x] Define the scanner plugin contract and execution lifecycle boundary.
- [x] Add a core in-process scheduler with bounded concurrency, cancellation, retry backoff, and tests.
- [x] Harden scheduler with duplicate-run rejection and stale execution sweeping.
- [x] Add scanner-specific concurrency limits and execution timeout status handling.
- [x] Implement scheduling, retries, cancellation, stale-job handling, and Kubernetes Job execution.
- [x] Add deterministic Kubernetes Job manifest generation with scanner/mode identity labels and cleanup TTL.
- [x] Add Kubernetes Job executor lifecycle boundary for create, cancel, and stale-job cleanup.
- [x] Add authenticated-role-aware execution detail cancellation endpoint for queued/running local state.
- [x] Implement TruffleHog full scans, safe structured parsing, deduplication, and full-scan finding reconciliation.
- [x] Add executable TruffleHog full/incremental command adapter with safe structured parsing and deduplication.
- [x] Preserve managed repository identity through prepared workspaces to prevent finding attribution errors.
- [x] Add execution history, findings API, and server-backed detail endpoints.
- [x] Add `database/sql` PostgreSQL persistence adapter methods for executions, findings, checkpoints, and audit events.
- [x] Add PostgreSQL migration runner and environment-gated real-database checkpoint integration test.
- [x] Add PostgreSQL repository persistence and execution/finding history query methods.
- [x] Add role-aware read endpoints for execution history and findings.
- [x] Add bounded `page`/`pageSize` pagination to repository, run, and finding read endpoints.
- [x] Serve health, metrics, repository, execution, and findings routes from the runnable Go entrypoint.

## Phase 3 — Incremental scanning

- [x] Persist successful scan checkpoints and calculate commit ranges through the application and PostgreSQL adapter.
- [x] Define checkpoint persistence schema and tested commit-range calculation.
- [x] Implement incremental TruffleHog execution and periodic full-scan policy.
- [x] Add explicit periodic full-scan policy decision logic with tests.
- [x] Add application-level automatic scan-mode selection with incremental default and periodic full override.
- [x] Integrate periodic full-scan mode selection and successful full-scan timestamp persistence into ScanService.
- [x] Add manual full/incremental run selection with confirmation for full scans and API queue submission.

## Phase 4 — Renovate

- [x] Implement the Renovate plugin using prepared workspaces and shared Git objects.
- [x] Capture Renovate status, logs, and update/PR summaries.
- [x] Carry sanitized Renovate logs and update/error summary fields through plugin results and PostgreSQL execution storage.

## Phase 5 — Enterprise UX

- [x] Build the responsive dark dashboard visual slice from the supplied reference.
- [x] Add repository search, date range controls, notification state, and scan-launch interaction.
- [x] Add animated metrics, trend visualizations, run history, findings, activity, and system health panels.
- [x] Add honest placeholder states for repository, runs, findings, policies, alerts, and settings feature views.
- [x] Replace decorative glyph icons with accessible inline SVG primitives and improve dialog semantics.
- [x] Replace static dashboard metrics and charts with live repository, execution, and finding state.
- [x] Implement repository creation, run cancellation, settings persistence, and live alert derivation.
- [x] Make primary navigation and administration destinations functional with actionable empty states.
- [x] Add end-to-end coverage for repository creation, settings, cancellation, and all navigation destinations.
- [x] Add dark/light mode, URL-addressable filters, pagination, accessible keyboard flows, and role-aware actions.
- [x] Add dark/light mode, URL-addressable repository filtering, full-scan confirmation, and role-aware run actions.
- [x] Add OIDC, Admin/Operator/Viewer RBAC, audit trail, and runtime settings plane.
- [x] Add domain role/permission matrix and an audit event store foundation.
- [x] Add typed runtime settings with validation, provenance, in-memory store, and PostgreSQL schema.
- [x] Add role-aware runtime settings GET/PUT API endpoints with validation.
- [x] Add typed OIDC configuration and injectable bearer-token authentication middleware boundary.
- [x] Add JWT issuer/audience validation and OIDC role-claim extraction.
- [x] Add OIDC group-array claim mapping to Admin/Operator/Viewer roles.
- [x] Add explicit authenticated API-handler composition while leaving health probes public.
- [x] Add remote RSA JWKS retrieval for configured OIDC token verification.
- [x] Wire configured OIDC/JWKS authentication into the runnable API entrypoint with fail-fast partial configuration checks.
- [x] Add Docker Compose runtime with PostgreSQL, k3s, API, and reverse-proxied web services.
- [x] Add explicit Compose-only demo repository bootstrap for deterministic end-to-end smoke tests.
- [x] Add a repeatable Compose smoke-test script covering API, UI, PostgreSQL, and k3s readiness.
- [x] Validate `docker compose up` and full Compose end-to-end flow.
- [x] Add server-side role extraction from authenticated request principals.
- [x] Restrict the development role header to explicit opt-in and keep production default read-only.
- [x] Record manual runs, cancellations, and settings changes and expose paginated admin audit reads.

## Phase 6 — Hardening and verification

- [x] Add Go unit, controller, plugin, scheduler, cache, and PostgreSQL integration tests.
- [x] Add frontend component/feature and permission tests plus end-to-end coverage.
- [x] Add dependency-free repository search feature tests.
- [x] Add frontend role-permission unit coverage for Admin/Operator/Viewer scan actions.
- [x] Add rendered dashboard permission coverage for viewer/operator scan visibility.
- [x] Add Playwright end-to-end coverage for viewer restrictions and confirmed operator full-scan launch.
- [x] Add keyboard Escape dismissal for the scan dialog and cover it end-to-end.
- [x] Add repository, runs, findings, policy, alert, and settings feature views using the shared dashboard data model.
- [x] Add health/readiness endpoints, metrics, security hardening, and operational documentation.
- [x] Add health/readiness endpoints, baseline Prometheus metric, security headers, and operational documentation.
- [x] Add required architecture, development, installation, configuration, troubleshooting, and ADR documentation.
- [x] Add Helm Service, ServiceAccount/RBAC, optional Git-cache PVC, NetworkPolicy, and health probes.
- [x] Package the Repository, RepositorySource, and ScanPolicy CRDs in the Helm chart for install/upgrade workflows.
- [x] Validate rendered Helm resources and CRD inclusion with `helm template --include-crds`.
- [x] Add upgrade-safe Helm configuration for ingress, resources, pod scheduling, image pulls, and optional OIDC environment wiring.
- [x] Complete Helm deployment upgrade behavior and validate against a live Rancher Desktop Kubernetes cluster.
- [x] Dispatch the requested read-only Claude Sonnet 5 review and save its result/status to `claude-review.md`; do not action findings. (Blocked by Claude CLI model resolution and expired OAuth.)

## Verification log

- [x] Existing frontend production build passes.
- [x] Implemented the critical/high findings from code-suggested-changes.md: secure local Git workspaces, validated clone transports, fail-closed auth, stable discovery IDs, unique execution IDs, correct TruffleHog metadata/branch handling, tagged API contracts, audit timestamps/metadata, and execution timestamps.
- [x] Implemented medium/lower hardening: migration locking/version table support, paginated API envelopes and bounded persistence reads, stale scheduler cleanup, safe workspace release, shared bounded log sanitization, HTTP timeouts/graceful shutdown, JWKS caching/refresh, consistent auth errors, request body limits, Helm database/security/network-policy wiring, CI frontend/build/chart checks, and artifact ignores.
- [x] Verified Go tests, frontend build/unit tests, Helm lint/template, and Rancher Desktop Compose end-to-end tests.
- [x] Implemented the recommendations in `code-suggested-changes.md` and reverified the application, scanner adapters, API contracts, and Helm manifests.
- [x] Re-run production build after the remaining implementation changes.
- [x] Review the running app in a browser at desktop and mobile widths.
- [x] Remove the dashboard favicon 404 and confirm a clean browser resource load.
