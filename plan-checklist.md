# `plan.md` execution checklist

This is the live completion record for the enterprise-readiness plan. An item is
checked only after implementation and a matching verification step exists.

## Phase 1 — Persistence

- [x] PostgreSQL pool, startup ping, startup migrations, and readiness ping
- [x] Embedded ordered migrations with schema-version tracking and advisory lock
- [x] Durable API state adapter for repositories, executions, findings, settings, scanners, and audit events
- [x] Repository foreign keys/cascades and UI query indexes
- [x] Startup reconciliation for `preparing`/`running` executions
- [x] Runtime settings and scanner definitions persistence
- [x] Configurable connection-pool sizing and documentation

## Phase 2 — Multi-instance correctness

- [x] PostgreSQL repository advisory-lock helper for cache/scheduler dedupe
- [x] Kubernetes Lease leader election for singleton retention/maintenance loop
- [x] Configurable git-cache PVC access mode
- [x] Idempotency key/dedupe for execution submission

## Phase 3 — Security and compliance

- [x] Secret-backed private repository credentials through Kubernetes Jobs
- [x] Clone URL validation and Git protocol hardening
- [x] Shared bounded scanner-log sanitizer at persistence/display boundaries
- [x] Restricted pod security context defaults
- [x] Request-size limits, strict JSON decoding, and enum validation
- [x] CSP, HSTS, and Permissions-Policy headers
- [x] Dependency, image, and secret scanning gates in CI
- [x] Exact frontend dependency versions and lockfile
- [x] Cosign image signing, Syft SBOM, and multi-architecture release publishing workflow
- [x] Audit authorization denials and authentication failures; filter audit reads by actor/action/outcome
- [x] Vulnerability disclosure policy
- [x] Per-principal API rate limiting on run submission and discovery routes

## Phase 4 — Enterprise identity and access

- [x] Explicit OIDC-only identity constraint and gateway guidance documented
- [x] Documented manual provisioning/deprovisioning guidance
- [x] Persistent role/group mappings through runtime settings, verifier reload, and dedicated Roles UI
- [x] Token expiration remains issuer/JWT-controlled, bearer revocation/logout and configurable API IP allowlisting are supported
- [x] Explicitly document single-team scope/non-goal

## Phase 5 — Observability and operability

- [x] Structured application logging with execution/repository/scanner fields
- [x] Request/correlation ID middleware and response/context propagation
- [x] Real Prometheus request counters and cumulative duration metrics
- [x] Database-backed readiness check
- [x] ServiceMonitor Helm template
- [x] On-call runbooks for top production failures
- [x] Retention enforcement and Git-cache eviction scheduled daily
- [x] Typed domain error taxonomy with scheduler retry-policy hook

## Phase 6 — API contract and integrations

- [x] OpenAPI 3 contract served and CI-validated
- [x] Stable API DTO package applied to primary list responses
- [x] Runs/findings filtering, sorting, and paginated API envelopes
- [x] Generic outbound webhook on Kubernetes-backed scan completion
- [x] Findings CSV/PDF compliance report endpoints and scheduled daily filesystem exports

## Phase 7 — Deployment completeness

- [x] UI included in Helm deployment
- [x] External PostgreSQL secret wiring
- [x] Document startup-migration upgrade path
- [x] Helm values schema, NOTES, HPA, scanner defaults, and CRD printer columns
- [x] CRD upgrade documentation

## Phase 8 — CI/CD

- [x] golangci-lint with security/static analysis linters
- [x] Race tests and `go vet`
- [x] govulncheck, npm audit, Trivy, and Gitleaks CI gates
- [x] Container build in CI
- [x] Playwright CI invocation added
- [x] Multi-arch GHCR release workflow with cosign/SBOM and OCI Helm chart publication
- [x] CI concurrency and Go/npm caching
- [x] Remove committed rendered Helm evidence artifacts

## Phase 9 — Frontend architecture and UX

- [x] TypeScript migration completed
- [x] Modal focus trap, skip link, and automated axe critical-violation audit
- [x] Loading/skeleton states for async views
- [x] Runs/findings search, filter, sort, and pagination controls
- [x] Notification center connected to persisted outbound webhook settings
