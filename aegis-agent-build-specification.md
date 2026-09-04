# Aegis --- Agent Build Specification

> **Purpose:** This document is the implementation brief for an
> autonomous coding agent. Build the product described here. Treat the
> architectural boundaries, repository structure, MVP scope, and
> engineering principles as requirements unless a change is technically
> necessary and documented.

## 1. Product Vision

Build **Aegis**, a Kubernetes-native repository security and automation
platform.

Aegis should take the operational model that makes **Renovate Operator**
useful---repository discovery, scheduling, Kubernetes Jobs, status
visibility, and a clean operator-focused UI---and generalize it into a
security-focused platform.

The first two engines are:

-   **Renovate** --- dependency discovery and automated dependency
    updates.
-   **TruffleHog** --- secret discovery and verification.

Aegis is **not** intended to reimplement Renovate or TruffleHog. It
orchestrates existing tools, provides shared repository infrastructure,
central configuration, history, access control, observability, and a
rich UI.

The initial release must remain deliberately small:

1.  Kubernetes operator/control plane.
2.  Web/API backend.
3.  React web UI.
4.  Shared persistent Git repository cache.
5.  PostgreSQL.
6.  Renovate integration.
7.  TruffleHog integration.
8.  Helm chart.

Do not introduce Kafka, Redis, RabbitMQ, Elasticsearch, Loki, a service
mesh, or other infrastructure unless the implementation cannot
reasonably function without it.

------------------------------------------------------------------------

## 2. Primary Design Goals

### 2.1 Kubernetes-native

Aegis runs in Kubernetes and uses Kubernetes primitives where they are
appropriate.

Use CRDs for **desired state and reconciliation**, not as a
general-purpose historical database.

Use Kubernetes Jobs for isolated scanner execution where practical.

### 2.2 One repository clone, many tools

A fundamental requirement is to avoid each scanner independently cloning
the same repository.

A repository should have one canonical cached Git mirror/cache managed
by Aegis. Renovate, TruffleHog, and future plugins must consume
repositories through the shared repository subsystem.

The normal lifecycle should be:

``` text
Repository discovered
       │
       ▼
Shared Git cache
       │
       ├── initial clone/fetch
       │
       └── subsequent incremental fetches
                 │
                 ▼
          Scan workspaces
            ├── Renovate
            └── TruffleHog
```

Scanner plugins **must not implement their own repository cloning
logic**.

### 2.3 Incremental by default, full scans when required

Aegis must support both:

-   **Incremental scan** --- process only relevant changes since the
    last successful scan.
-   **Full scan** --- scan the complete configured repository/history
    scope.

Incremental scanning should be the default where the underlying tool
supports it.

Full scans must be available:

-   manually;
-   on a schedule;
-   per repository;
-   per policy;
-   per scanner;
-   after configuration changes where a full rescan is necessary.

Do not pretend incremental scanning is supported where a scanner cannot
safely provide it. The plugin should expose its capabilities.

### 2.4 Enterprise-grade behavior without enterprise-grade complexity

Prioritize:

-   reliability;
-   clear status;
-   RBAC;
-   auditability;
-   safe concurrency;
-   retries;
-   predictable scheduling;
-   useful metrics;
-   operational visibility;
-   straightforward installation and upgrades.

Avoid premature distributed-system complexity.

------------------------------------------------------------------------

## 3. High-Level Architecture

``` text
                    ┌─────────────────────────┐
                    │        React UI         │
                    └────────────┬────────────┘
                                 │
                                 ▼
                    ┌─────────────────────────┐
                    │      Aegis API          │
                    │ RBAC / Settings / Data  │
                    └────────────┬────────────┘
                                 │
                 ┌───────────────┼────────────────┐
                 │               │                │
                 ▼               ▼                ▼
        ┌────────────────┐ ┌───────────┐ ┌────────────────┐
        │ Kubernetes API │ │ PostgreSQL│ │ Operator       │
        │ CRDs / Jobs    │ │ History   │ │ Controllers    │
        └────────────────┘ └───────────┘ └───────┬────────┘
                                                  │
                                                  ▼
                                      ┌────────────────────┐
                                      │ Shared Git Cache   │
                                      └─────────┬──────────┘
                                                │
                                  ┌─────────────┴─────────────┐
                                  ▼                           ▼
                           Renovate Job                TruffleHog Job
```

The API and operator may initially be packaged from the same Go
codebase/binary if that keeps the MVP simpler, but their application
boundaries must remain explicit.

------------------------------------------------------------------------

## 4. Source of Truth and Data Ownership

### Kubernetes CRDs

CRDs contain desired state and concise current reconciliation status.

Examples:

-   repositories and repository discovery configuration;
-   scanner enablement;
-   scan policies;
-   schedules;
-   concurrency configuration;
-   references to credentials;
-   desired scan mode;
-   current reconciliation conditions.

Do **not** put unbounded scan history, logs, findings, or dashboard
datasets into CRD status.

### PostgreSQL

PostgreSQL stores operational and historical application data:

-   scan executions;
-   scan attempts;
-   timestamps and duration;
-   result summaries;
-   TruffleHog findings;
-   finding state;
-   Renovate execution summaries;
-   failures and normalized errors;
-   audit events;
-   user/action history;
-   repository historical state;
-   last successful scan checkpoints;
-   data required for dashboards and reporting.

### Kubernetes Secrets

Credentials must be referenced from Kubernetes Secrets or external
secret mechanisms. Never persist raw Git credentials or scanner secrets
in PostgreSQL or CRD status.

------------------------------------------------------------------------

## 5. Kubernetes API / CRDs

Start with a **small CRD surface**. Do not create a CRD for every
database entity.

Recommended initial resources:

### `Repository`

Represents a repository Aegis manages.

Conceptual fields:

``` yaml
spec:
  provider: github
  url: https://github.example.com/org/repository
  credentialSecretRef: ...
  enabled: true
  scanners:
    renovate:
      enabled: true
    trufflehog:
      enabled: true
  policyRef: default
```

Status should contain only concise state such as:

-   Ready / Error / Disabled;
-   last synchronization;
-   latest observed commit SHA;
-   cache state;
-   latest scan state;
-   conditions.

### `RepositorySource`

Defines automatic repository discovery from a Git
provider/organization/group.

It should support:

-   provider configuration;
-   organization/group scope;
-   include/exclude patterns;
-   archived repository handling;
-   discovery schedule;
-   credential Secret reference.

Discovery should reconcile repositories into managed `Repository`
resources or equivalent desired-state records.

### `ScanPolicy`

Defines reusable scan behavior:

-   enabled scanners;
-   incremental/full defaults;
-   schedules;
-   periodic full scan schedule;
-   timeout;
-   retries;
-   concurrency-related policy;
-   scanner-specific configuration.

### Avoid unnecessary CRDs

Do not initially create persistent `ScanJob`, `Finding`, or
`ScannerPlugin` CRDs simply because the product is an operator.

Historical executions and findings belong in PostgreSQL. Actual
executions can use native Kubernetes `Job` resources with Aegis
labels/annotations and owner relationships where appropriate.

Scanner plugins should initially be code-level interfaces/configuration.
A dynamic plugin CRD can be introduced later if there is a genuine
requirement for runtime-installed plugins.

------------------------------------------------------------------------

## 6. Repository Discovery

Repository discovery should follow the principles of Renovate Operator.

The system must be able to:

1.  connect to a configured Git provider;
2.  enumerate repositories in the configured scope;
3.  apply include/exclude filters;
4.  detect newly created repositories;
5.  detect archived/removed repositories;
6.  create/update the Aegis desired state;
7.  schedule eligible work without creating duplicate executions.

Design provider interfaces so GitHub is the first implementation but
GitLab/Bitbucket can be added without changing core scheduling logic.

Do not couple the domain layer directly to a specific Git provider SDK.

------------------------------------------------------------------------

## 7. Shared Git Cache

This is a core subsystem, not an implementation detail.

### Requirements

-   Exactly one canonical cached repository per repository identity.
-   Persistent storage via PVC.
-   Initial clone/fetch once.
-   Subsequent synchronization through incremental `git fetch`.
-   Safe concurrent access.
-   Per-repository locking.
-   No scanner may mutate the canonical cache.
-   Scanner executions receive an isolated workspace derived from the
    cache.
-   Cleanup of temporary workspaces.
-   Configurable cache size/retention.
-   Cache health/status visible to the application.
-   Recovery from corrupt/incomplete caches.
-   Metrics for cache hits, misses, fetch duration, size and errors.

Prefer a **bare mirror/object cache + disposable worktree/workspace**
model rather than sharing one mutable working tree between scanner jobs.

### Concurrency

Two workers must never corrupt a repository cache by fetching or
mutating it concurrently.

Implement explicit locking/leases and test this behavior.

### Distributed scheduling

The design must work correctly when the operator/API has multiple
replicas.

Do not rely on in-memory locks for cluster-wide correctness. Use
Kubernetes coordination primitives, database coordination, or another
mechanism already present in the architecture.

------------------------------------------------------------------------

## 8. Scanner/Automation Plugin Architecture

Create a small, explicit plugin contract.

Conceptually:

``` go
type Plugin interface {
    Name() string
    Capabilities() Capabilities
    ValidateConfig(...) error
    BuildExecution(...) (...)
    ParseResult(...) (...)
}
```

Capabilities should describe relevant behavior, for example:

-   supports full scan;
-   supports incremental scan;
-   requires writable workspace;
-   produces findings;
-   produces remediation/change output.

Core orchestration must not contain TruffleHog-specific or
Renovate-specific branching beyond plugin registration/configuration.

### Initial plugins

#### TruffleHog

Support:

-   full repository/history scans;
-   incremental scans over an appropriate commit range;
-   structured result parsing;
-   verified/unverified result information where provided by TruffleHog;
-   deduplication of findings;
-   finding lifecycle/history;
-   execution timeout and cancellation;
-   safe handling of output so discovered secrets are not accidentally
    exposed in logs.

**Never store or display complete secret values.** Store only the
minimum evidence/fingerprint required to identify and manage a finding.

#### Renovate

Use the official Renovate engine rather than reimplementing dependency
logic.

Support:

-   execution against managed repositories;
-   existing Renovate configuration patterns where practical;
-   job result/status capture;
-   logs;
-   PR/update summary information;
-   manual execution;
-   scheduled execution.

The integration must be designed around the shared Git subsystem. If
Renovate has constraints that prevent safe reuse of the exact same
workspace, reuse the cached Git objects/mirror to avoid a second network
clone and create an isolated Renovate working copy.

Document any unavoidable Renovate limitations rather than hacking around
them.

------------------------------------------------------------------------

## 9. Scheduling and Execution

The scheduler is one of the most important parts of Aegis.

Support:

-   repository discovery schedules;
-   per-policy schedules;
-   per-scanner schedules;
-   manual runs;
-   full vs incremental selection;
-   configurable global concurrency;
-   scanner-specific concurrency;
-   repository-level mutual exclusion where required;
-   timeout;
-   retry with backoff;
-   cancellation;
-   stale job detection;
-   Kubernetes Job cleanup/retention;
-   prevention of duplicate work.

The UI must clearly distinguish:

-   queued;
-   preparing repository;
-   running;
-   succeeded;
-   failed;
-   canceled;
-   timed out;
-   skipped.

A failed TruffleHog scan is different from a successful scan that found
secrets. Model these separately.

------------------------------------------------------------------------

## 10. Authentication and Authorization

Support external authentication through **OIDC/OAuth2**, with OIDC as
the primary enterprise path.

Do not build a local password system for the MVP unless required for
development mode.

### Roles

#### Admin

Can:

-   view all repositories, scans, findings and dashboards;
-   configure system settings;
-   configure repository sources;
-   configure policies and scanners;
-   trigger/cancel runs;
-   manage user/group role mappings;
-   manage retention and operational settings.

#### Operator

Can:

-   view repositories, findings, scans and dashboards;
-   manually trigger permitted scans/runs;
-   choose full or incremental mode when triggering;
-   retry/cancel permitted jobs.

Cannot modify administrative/system configuration.

#### Viewer

Read-only access to:

-   dashboards;
-   repository status;
-   scan history;
-   findings/results they are permitted to see;
-   metrics exposed through the application.

### Authorization requirements

Authorization must be enforced server-side. Hiding a UI button is not
authorization.

Support mapping OIDC groups/claims to Aegis roles.

------------------------------------------------------------------------

## 11. Audit Logging

Persist audit events in PostgreSQL for meaningful user/system actions,
including:

-   settings changes;
-   policy changes;
-   repository source changes;
-   manual scan execution;
-   full scan requests;
-   cancellations/retries;
-   RBAC changes.

Capture:

-   actor;
-   action;
-   target;
-   timestamp;
-   outcome;
-   relevant non-secret metadata.

Provide an admin UI for searching/filtering audit history.

------------------------------------------------------------------------

## 12. UI/UX Direction

The UI must be **specifically inspired by the Renovate Operator UI**,
not the Renovate dependency bot website.

Use it as the conceptual reference for:

-   operator-oriented navigation;
-   repository-centric workflows;
-   job/run visibility;
-   status-driven presentation;
-   concise operational information.

Do not make a pixel-for-pixel clone. Build a distinct Aegis security
product.

### Frontend stack

Use:

-   **React 19**
-   **TypeScript**
-   **Vite**
-   **Material UI (MUI)** for the component/design system
-   **TanStack Query** for server state, caching, retries and
    invalidation
-   **TanStack Table** for rich data grids/tables where MUI's basic
    tables are insufficient
-   **Apache ECharts** through a maintained React integration for rich
    interactive charts
-   **React Router**
-   **React Hook Form + Zod** for forms and validation

Keep frontend dependencies intentional. Do not install multiple
libraries that solve the same problem.

### UX requirements

-   responsive desktop-first layout;
-   dark and light modes;
-   consistent status chips;
-   skeleton/loading states;
-   empty states;
-   actionable error states;
-   URL-addressable filters where useful;
-   pagination for large datasets;
-   search/filter/sort on repository, scan and finding views;
-   accessible components and keyboard navigation;
-   confirmation for destructive/expensive operations such as full
    rescans.

------------------------------------------------------------------------

## 13. UI Information Architecture

### Dashboard

The landing page should immediately answer:

-   Is Aegis healthy?
-   What has run recently?
-   What is failing?
-   Are security findings increasing or decreasing?
-   Which repositories need attention?

Include summary cards such as:

-   managed repositories;
-   scans in progress;
-   scans in queue;
-   successful scans;
-   failed scans;
-   active secret findings;
-   repositories with failures.

### Dashboard graphs

Use ECharts to provide rich, interactive visualizations.

Initial charts:

1.  **Executions over time**
    -   stacked/grouped by scanner;
    -   success/failure distinction;
    -   selectable time range.
2.  **Success vs failure rate**
    -   trend over time rather than only a static pie chart.
3.  **Scan duration**
    -   p50/p95 or useful duration trend;
    -   filter by scanner.
4.  **Secret findings over time**
    -   new;
    -   resolved;
    -   active.
5.  **Top repositories requiring attention**
    -   failures/findings.
6.  **Execution activity**
    -   queued/running/completed counts.

Do not overload the MVP dashboard with decorative charts. Every graph
must answer an operational question.

### Repositories

Repository list should show:

-   name/provider;
-   enabled scanners;
-   latest commit observed;
-   cache state;
-   last run;
-   last successful run;
-   current status;
-   active findings;
-   actions.

Repository detail page:

-   overview;
-   recent executions;
-   scanner status;
-   findings;
-   configuration/policy;
-   cache information;
-   activity/history;
-   manual **Run** action with scanner and full/incremental selection.

### Runs / Executions

Provide:

-   global execution list;
-   filters by repository, scanner, status, date;
-   execution detail;
-   timeline;
-   start/end/duration;
-   commit range;
-   scan mode;
-   retry number;
-   Kubernetes Job information;
-   normalized error;
-   sanitized logs;
-   retry/cancel actions according to RBAC.

### Findings

For TruffleHog findings provide:

-   repository;
-   detector/type;
-   verification status;
-   file/path where safely available;
-   commit reference;
-   first seen;
-   last seen;
-   state;
-   fingerprint;
-   links to relevant repository context where safe.

Never render the full discovered secret.

### Settings

Admins require a rich settings plane.

Organize settings into sections:

-   General
-   Git providers / repository discovery
-   Scheduling
-   Git cache
-   Renovate
-   TruffleHog
-   Authentication / OIDC
-   RBAC mappings
-   Data retention
-   Kubernetes execution
-   UI preferences where appropriate

Settings must have:

-   descriptions;
-   defaults;
-   validation;
-   safe secret references;
-   clear indication of whether a change requires
    restart/reconciliation.

------------------------------------------------------------------------

## 14. Configuration Model: Helm vs UI

The Helm chart must expose **all deployment/bootstrap settings required
to install and operate Aegis**.

However, do not create two competing sources of truth.

Use this rule:

### Helm owns bootstrap/deployment configuration

Examples:

-   image/repository/tag;
-   replicas;
-   resources;
-   service;
-   ingress;
-   security contexts;
-   service accounts;
-   persistence/PVC;
-   PostgreSQL connection/deployment configuration;
-   pod scheduling;
-   TLS;
-   initial OIDC bootstrap;
-   environment-specific Kubernetes settings.

### Aegis settings plane owns runtime application configuration

Examples:

-   scan schedules;
-   scanner defaults;
-   concurrency;
-   discovery configuration;
-   retention;
-   cache policy;
-   role mappings;
-   operational defaults.

Where a runtime setting has a Helm default, Helm supplies the
**initial/default value**, and Aegis stores subsequent runtime overrides
in its authoritative application configuration.

The UI must show:

-   effective value;
-   source (default/bootstrap/runtime override);
-   reset-to-default behavior where appropriate.

Document precedence explicitly.

------------------------------------------------------------------------

## 15. PostgreSQL Schema and Migrations

Use PostgreSQL from the beginning.

Use a migration system appropriate for Go and commit all migrations.

Likely domains/tables include:

``` text
repositories
repository_sync_state
executions
execution_attempts
findings
finding_occurrences
audit_events
application_settings
role_mappings
```

Do not mirror Kubernetes objects blindly into relational tables. Persist
only what the application needs for querying/history.

Database access must be isolated behind repository/data-access
interfaces rather than spread throughout HTTP handlers and controllers.

All high-volume dashboard queries must be indexable and paginated.

------------------------------------------------------------------------

## 16. API Design

Expose a versioned API, for example:

``` text
/api/v1/...
```

Use REST for the MVP.

Requirements:

-   consistent error format;
-   pagination;
-   filtering;
-   sorting;
-   request validation;
-   OpenAPI specification;
-   authorization middleware;
-   correlation/request IDs;
-   no secret leakage in responses;
-   stable DTOs separate from database models.

The UI must use the API. It must not communicate directly with
PostgreSQL or the Kubernetes API.

------------------------------------------------------------------------

## 17. Repository Structure

Create the repository structure **before implementing features**. Do not
allow the codebase to grow organically into arbitrary folders.

Use a monorepo:

``` text
aegis/
├── .github/
│   └── workflows/
├── api/
│   └── openapi/
├── cmd/
│   └── aegis/
│       └── main.go
├── config/
│   ├── crd/
│   ├── rbac/
│   ├── manager/
│   └── samples/
├── deploy/
│   └── helm/
│       └── aegis/
├── docs/
│   ├── architecture/
│   ├── development/
│   ├── operations/
│   └── adr/
├── internal/
│   ├── app/
│   │   ├── commands/
│   │   └── queries/
│   ├── auth/
│   ├── audit/
│   ├── cache/
│   │   ├── git/
│   │   ├── locking/
│   │   └── workspace/
│   ├── config/
│   ├── database/
│   │   ├── migrations/
│   │   └── postgres/
│   ├── discovery/
│   │   └── providers/
│   │       └── github/
│   ├── domain/
│   │   ├── repository/
│   │   ├── execution/
│   │   ├── finding/
│   │   └── policy/
│   ├── operator/
│   │   ├── controllers/
│   │   └── scheduler/
│   ├── plugins/
│   │   ├── plugin.go
│   │   ├── renovate/
│   │   └── trufflehog/
│   ├── server/
│   │   ├── handlers/
│   │   ├── middleware/
│   │   └── dto/
│   └── telemetry/
├── pkg/
│   └── api/
│       └── v1alpha1/
├── web/
│   ├── src/
│   │   ├── app/
│   │   ├── assets/
│   │   ├── components/
│   │   │   ├── charts/
│   │   │   ├── data-display/
│   │   │   ├── feedback/
│   │   │   ├── forms/
│   │   │   └── layout/
│   │   ├── features/
│   │   │   ├── dashboard/
│   │   │   ├── repositories/
│   │   │   ├── executions/
│   │   │   ├── findings/
│   │   │   ├── settings/
│   │   │   └── audit/
│   │   ├── hooks/
│   │   ├── lib/
│   │   ├── routes/
│   │   ├── services/
│   │   ├── types/
│   │   └── utils/
│   ├── tests/
│   └── package.json
├── tests/
│   ├── integration/
│   └── e2e/
├── Dockerfile
├── Makefile
├── go.mod
├── README.md
└── AGENTS.md
```

### Structure rules

-   `internal/domain` contains business concepts and must not depend on
    HTTP, PostgreSQL, Kubernetes controllers, or concrete Git providers.
-   `internal/app` contains application use cases/orchestration.
-   Infrastructure implements interfaces required by the
    domain/application layer.
-   HTTP handlers remain thin.
-   Kubernetes reconcilers remain thin.
-   Scanner implementations live behind the plugin contract.
-   React code is organized primarily by **feature**, with reusable
    generic UI primitives under `components`.
-   Do not create generic `helpers` or `utils` dumping grounds.
-   Avoid files that become enormous orchestration classes/functions.

------------------------------------------------------------------------

## 18. Engineering Principles

Apply these consistently.

### Separation of concerns

Controllers reconcile. Services/use cases orchestrate. Repositories
persist. Plugins integrate external engines. Handlers translate HTTP.

Do not mix all of these responsibilities in one package.

### Dependency inversion

Core logic depends on interfaces/ports, not GitHub SDKs, PostgreSQL
drivers, Kubernetes clients, Renovate internals, or TruffleHog
implementation details.

### Explicitness over abstraction

Do not build a framework for hypothetical future requirements.

Create abstractions only where there is already more than one
implementation, a clear external boundary, or an obvious testability
requirement.

### Small interfaces

Prefer narrow, capability-oriented Go interfaces.

Avoid large "manager" interfaces.

### Error handling

-   wrap errors with useful context;
-   classify expected operational errors;
-   expose safe normalized errors to the UI;
-   preserve detailed internal errors in logs;
-   never leak credentials/secrets.

### Idempotency

All reconciliation and scheduling logic must be idempotent.

Repeated reconciliation must not create duplicate scans.

### Context and cancellation

Propagate Go `context.Context` through I/O operations and honor
cancellation/timeouts.

### Configuration

-   typed configuration;
-   validation at startup/change time;
-   no magic environment variables scattered through the code;
-   documented defaults.

### Logging

Use structured logging.

Every execution should be traceable using fields such as:

-   repository ID;
-   execution ID;
-   scanner;
-   Kubernetes Job;
-   correlation ID.

### Security by default

-   non-root containers;
-   read-only root filesystem where possible;
-   least-privilege Kubernetes RBAC;
-   sanitized scanner output;
-   no secrets in logs;
-   no full discovered secrets persisted;
-   secure headers and cookies;
-   CSRF protections where applicable;
-   parameterized SQL;
-   validate all user input.

------------------------------------------------------------------------

## 19. Testing Requirements

Testing is part of the implementation, not a later phase.

### Go

Use:

-   unit tests for domain/application logic;
-   controller reconciliation tests;
-   plugin contract tests;
-   Git cache concurrency tests;
-   PostgreSQL integration tests;
-   scheduler/idempotency tests.

### Frontend

Use:

-   Vitest;
-   React Testing Library;
-   component/feature tests;
-   permission/role tests.

### End-to-end

Use Playwright for a small number of critical workflows:

1.  viewer can inspect but cannot trigger/configure;
2.  operator can trigger a scan but cannot change admin settings;
3.  admin can change runtime settings;
4.  repository discovery → execution → result visible in UI;
5.  failed execution clearly differs from security findings.

### Git cache tests are mandatory

Test:

-   first clone;
-   subsequent fetch;
-   cache hit;
-   concurrent requests;
-   workspace isolation;
-   corrupt cache recovery;
-   cleanup;
-   incremental commit range determination.

------------------------------------------------------------------------

## 20. Observability

Keep observability built in but do not add separate infrastructure.

Expose a Prometheus-compatible `/metrics` endpoint.

Metrics should include:

-   repository count;
-   discovery runs/errors;
-   queued/running executions;
-   execution totals by scanner/status;
-   execution duration;
-   scan failures;
-   Git fetch duration/errors;
-   cache hit/miss;
-   cache size;
-   active findings;
-   API request duration/error count.

The Aegis UI obtains its historical graphs from PostgreSQL/API, not by
requiring Prometheus to be installed.

Also expose:

-   `/healthz`
-   `/readyz`

------------------------------------------------------------------------

## 21. Helm Chart Requirements

The Helm chart is a first-class deliverable.

It must support configuration for all deploy-time defaults, including:

-   Aegis image;
-   replicas;
-   resources;
-   service;
-   ingress;
-   service account;
-   Kubernetes RBAC;
-   pod/container security context;
-   nodeSelector;
-   affinity;
-   tolerations;
-   topology spread;
-   imagePullSecrets;
-   Git cache PVC size/storageClass/access mode;
-   PostgreSQL deployment or external PostgreSQL connection;
-   database Secret references;
-   OIDC bootstrap;
-   scanner images/versions;
-   job resources;
-   concurrency defaults;
-   retention defaults;
-   probes;
-   network policy where practical.

Provide a well-commented `values.yaml`.

Do not hard-code cluster-specific values.

------------------------------------------------------------------------

## 22. Deployment Model

For the MVP, prefer a small number of deployable components:

``` text
aegis
├── Aegis operator/API deployment
├── Aegis web UI (may be served by API or separate lightweight deployment)
├── shared Git cache PVC
├── PostgreSQL
└── ephemeral Kubernetes scanner Jobs
```

If serving the compiled React application from the Go API significantly
reduces operational complexity, that is acceptable and preferable for
the first release.

Scanner jobs may use dedicated images containing the required tool, but
orchestration/control remains in Aegis.

------------------------------------------------------------------------

## 23. MVP User Flows

The MVP is not complete until these flows work.

### Flow 1 --- Repository discovery

``` text
Admin configures GitHub source
→ Aegis discovers repositories
→ Repository resources appear
→ repositories appear in UI
```

### Flow 2 --- First TruffleHog scan

``` text
Repository selected
→ Aegis populates Git cache
→ isolated workspace created
→ TruffleHog Job runs
→ execution stored
→ findings safely normalized
→ dashboard/repository UI updates
```

### Flow 3 --- Incremental TruffleHog scan

``` text
New commit arrives
→ git fetch updates existing cache
→ previous successful checkpoint located
→ only required commit range scanned
→ new execution/result persisted
```

No full network reclone should occur during the normal path.

### Flow 4 --- Full rescan

``` text
Operator selects Run
→ selects TruffleHog + Full
→ confirmation
→ full scan executes
→ execution explicitly labeled Full
```

### Flow 5 --- Renovate

``` text
Repository eligible for Renovate
→ cached repository data reused
→ Renovate executes
→ result/status captured
→ run visible in UI
```

### Flow 6 --- Failure

``` text
Scanner exits unsuccessfully
→ execution = Failed
→ normalized error stored
→ dashboard failure count changes
→ repository shows attention state
→ retry available to authorized user
```

This must **not** be represented as a security finding.

------------------------------------------------------------------------

## 24. Documentation Required During Development

Maintain documentation as the code is built.

At minimum:

``` text
README.md
docs/architecture/overview.md
docs/architecture/git-cache.md
docs/architecture/scanner-plugins.md
docs/architecture/data-ownership.md
docs/development/getting-started.md
docs/operations/installation.md
docs/operations/configuration.md
docs/operations/troubleshooting.md
docs/adr/
AGENTS.md
```

Use Architecture Decision Records for meaningful choices that future
agents/developers should not have to rediscover.

`AGENTS.md` should explain:

-   repository map;
-   architectural boundaries;
-   where new features belong;
-   testing commands;
-   build commands;
-   common development workflows;
-   important invariants such as "scanners never clone repositories
    themselves."

Keep documentation updated in the same change that changes architecture.

------------------------------------------------------------------------

## 25. Implementation Phases

Build vertically. Do not build every backend subsystem before creating a
usable product.

### Phase 0 --- Foundation

-   establish repository structure;
-   initialize Go/Kubebuilder project;
-   initialize React application;
-   establish linting/formatting/testing;
-   create Helm chart skeleton;
-   PostgreSQL migrations;
-   CI;
-   architecture documentation.

### Phase 1 --- Repository management

-   Repository CRD;
-   RepositorySource CRD;
-   GitHub provider;
-   discovery reconciliation;
-   basic repository API/UI;
-   shared Git cache;
-   cache tests.

### Phase 2 --- Execution framework + TruffleHog

-   plugin contract;
-   scheduler;
-   Kubernetes Job runner;
-   TruffleHog plugin;
-   full scans;
-   execution history;
-   findings;
-   execution/repository UI.

### Phase 3 --- Incremental scanning

-   successful scan checkpoints;
-   commit range calculation;
-   incremental TruffleHog;
-   configurable periodic full scans;
-   manual full/incremental selector.

### Phase 4 --- Renovate

-   Renovate plugin;
-   reuse Git cache/object data;
-   execution capture;
-   UI integration.

### Phase 5 --- Enterprise UX

-   OIDC;
-   Admin/Operator/Viewer RBAC;
-   audit trail;
-   settings plane;
-   dashboard charts;
-   richer filtering;
-   dark mode.

### Phase 6 --- Hardening

-   HA/controller leader election;
-   concurrency/load tests;
-   recovery behavior;
-   security review;
-   upgrade/migration tests;
-   Helm polish;
-   operational documentation.

Each phase must leave the repository buildable and testable.

------------------------------------------------------------------------

## 26. Definition of Done

Aegis MVP is done when:

-   it installs through Helm into a Kubernetes cluster;
-   GitHub repositories can be discovered automatically;
-   a repository is not repeatedly network-cloned for each scanner;
-   the Git cache survives pod restarts when persistence is configured;
-   normal updates use incremental fetch;
-   TruffleHog can perform full and incremental scans;
-   Renovate can be orchestrated;
-   executions are Kubernetes-native and observable;
-   scan history survives Kubernetes Job deletion;
-   findings survive pod restarts;
-   PostgreSQL is the historical/query store;
-   CRDs remain bounded desired-state resources;
-   Admin, Operator and Viewer permissions are enforced server-side;
-   admins can configure runtime behavior in the UI;
-   operators can trigger full/incremental executions;
-   viewers are read-only;
-   the dashboard clearly shows what ran, when, what succeeded, and what
    failed;
-   rich charts show useful execution/security trends;
-   failures are distinguishable from findings;
-   audit events are recorded;
-   no complete discovered secret is persisted or exposed;
-   Helm exposes all required deployment/bootstrap configuration;
-   the code follows the prescribed architectural boundaries;
-   automated tests cover the critical scheduling, cache and
    authorization paths;
-   architecture and operator documentation are current.

------------------------------------------------------------------------

## 27. Explicit Non-Goals for the First Release

Do **not** expand the initial build into:

-   a SIEM;
-   a generic CI/CD engine;
-   a vulnerability management suite;
-   a replacement for GitHub/GitLab;
-   a secret rotation platform;
-   a custom secrets scanner;
-   a custom dependency update engine;
-   a Kafka/event-streaming architecture;
-   a generic runtime plugin marketplace;
-   multi-cluster orchestration;
-   complex multi-tenancy.

Design clean extension points, but ship the focused product first.

------------------------------------------------------------------------

## 28. Instructions to the Coding Agent

Before writing significant feature code:

1.  Read this specification completely.
2.  Create the prescribed repository structure.
3.  Write `docs/architecture/overview.md`.
4.  Record any deliberate deviation from this specification as an ADR.
5.  Create a short implementation plan mapped to the phases above.
6.  Implement one vertical slice at a time.
7.  Run tests and linters after every meaningful change.
8.  Do not silently weaken requirements to make tests pass.
9.  Do not introduce additional infrastructure without documenting why
    the existing architecture cannot satisfy the requirement.
10. Keep the application runnable throughout development.

When uncertain, prefer:

**simple \> clever**\
**explicit \> magical**\
**bounded CRDs \> etcd as a database**\
**shared Git data \> duplicate network clones**\
**small interfaces \> framework abstractions**\
**operational clarity \> feature count**

The core invariant is:

> **Aegis owns repository acquisition and caching. Scanner plugins
> consume prepared repository workspaces; they do not independently
> clone repositories.**
