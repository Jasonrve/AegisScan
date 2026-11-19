# Scanner catalog and shared workspaces

AegisScan ships with two enabled scanner definitions: `trufflehog` and
`renovate`. Administrators can change their container image and startup
parameters from Settings or through the scanner catalog API:

- `GET /api/v1/scanners` — view enabled definitions
- `POST /api/v1/scanners` — add a custom definition
- `PUT /api/v1/scanners/{id}` — update a definition
- `DELETE /api/v1/scanners/{id}` — remove a custom definition
- `POST /api/v1/scanners/preview-parse` — dry-run the output parser on a pasted sample

Custom scanners must declare how their output is interpreted (SARIF or Aegis JSON): see [custom-scanners.md](custom-scanners.md).

Scanner arguments support these placeholders:

- `{{workspace}}` — the prepared repository path inside the scanner container
- `{{repository}}` — the managed Aegis repository ID
- `{{branch}}` — the prepared repository branch
- `{{mode}}` — `incremental` or `full`

Each scan runs as one Kubernetes Job (`aegisscan-<execution>-<attempt>`). A `clone`
init container prepares `/workspace/repo` (shallow, `--depth 50`, for incremental
scans; the complete history for `full` scans) and prints the checked-out commit,
which AegisScan records on the repository and its findings. Scanner containers
never clone repositories themselves.

### Shared Git cache (ADR 0001)

Set `AEGIS_GIT_CACHE_PVC` (Helm: `gitCache.enabled=true`) to the name of a PVC and the
`clone` init container keeps one bare mirror per repository there
(`<repository>.git`), refreshed under `flock` so concurrent Jobs never corrupt it, and
makes each scan's private clone from the mirror. Without it every Job clones directly.
Use `ReadWriteMany` storage when Jobs can land on different nodes (`ReadWriteOnce` is
enough on a single node, as in the dev k3s: `deploy/dev/git-cache.yaml`). The PVC size is
the bound of the cache; repositories using it are marked `warm`.

### Incremental vs full scans

*Incremental* scans clone `--depth 50` and run TruffleHog in `filesystem` mode (the
checked-out files only). *Full*
scans clone the complete history and run `trufflehog git`, which finds secrets that were
deleted later and reports the **commit that introduced each one** (the file link then
points at that revision). Because deleting a secret from the files does not make it safe, **only a full scan
auto-resolves secret findings**; incremental scans still auto-resolve dependency findings.
A repository is blocked only by *open* verified findings, and a blocked repository can only
be unblocked (or removed) by an admin; reconciling its `Repository` resource never lifts the block.

## Job security and limits

Scan pods run as non-root (uid 65534) with a read-only root filesystem, all
capabilities dropped, no service-account token, a RuntimeDefault seccomp
profile, CPU/memory requests and limits, and a 30 minute deadline. `/workspace`
and `/tmp` are the only writable paths. Scanner ids become the
`aegisscan.io/scanner` Job label, so they must be lowercase letters, digits or
`-` (max 63 characters). `Settings → Max concurrency` caps simultaneously
running Jobs.

## Results

- **TruffleHog** (`filesystem` scan, JSON): one finding per detector hit. The
  secret itself is never stored — only a fingerprint, file path and line.
  Findings are scoped severity-wise server-side: verified = critical,
  unverified private key = high, other unverified = medium.
- **Renovate** (`--platform=local --dry-run=lookup`, `LOG_LEVEL=debug`): the local
  platform creates no branches, so results are read from the
  `packageFiles with updates` debug line. Each dependency with an available
  update becomes a `RenovateDependency` finding (major = medium, otherwise low).
  Genuine repository config errors become `RenovateConfig` findings;
  environment limitations are warnings in the run output.
- To let Renovate look up GitHub-hosted dependencies (e.g. GitHub Actions),
  create the Secret `aegisscan-scanner-credentials` with key `github-token` in
  the scanner namespace. It is exposed as `GITHUB_COM_TOKEN` to the `renovate`
  scanner only.
- A finding that an **authoritative** rescan no longer reports is auto-resolved.
  A run is authoritative only when the scanner proves it saw everything
  (TruffleHog's `finished scanning` line; Renovate's `packageFiles with updates`
  line). Dependencies Renovate had to skip keep their existing finding open.
  Triaged findings (`ignored`, `false_positive`) are never re-opened by a rescan.
- The run output (`GET /api/v1/runs/{id}/logs`) is a sanitized, size-capped
  summary — raw scanner output is not stored, so secrets never reach the database.

## Renovate pull request mode and forge links

By default Renovate runs read-only (`--platform=local --dry-run=lookup`): updates are
reported as findings and nothing is written to the forge. Turn on **Policies → Renovate
opens pull requests** (setting `renovateCreatePullRequests`) to run Renovate against the
forge itself (GitHub, GitLab or Gitea): it opens one pull request per update plus the
*Dependency Dashboard* issue, and AegisScan links each finding to its pull request and
the run to all pull requests and the issue. This needs a forge token with repository and
issue write access in the Secret `aegisscan-scanner-credentials`, key `renovate-token`
(the operator checks the key exists before creating the Job; its RBAC is limited to that
one Secret by name). A closed pull request means "ignore this update" to Renovate and is
not recreated.

AegisScan builds links to the repository, commit, `file#Lline`, pull request and issue
per provider. When repositories are cloned over an internal address the browser cannot
reach, map it under **Settings → Forge URL mapping** (`internal => public`, setting
`forgeUrlOverrides`). Plain `http` clone URLs are rejected unless
`AEGIS_ALLOW_INSECURE_CLONE_URLS=true` (set in the dev compose file only).

Finding previews: for TruffleHog the preview is a masked one-line rendering (PEM headers
and already-masked values are kept; anything else shows a short prefix and its length) —
the secret and key material are never stored.

## Testing against a local Gitea

`docker compose up --build -d` also starts Gitea (a GitHub stand-in, UI on
`http://localhost:3300`, `aegis` / `Aegis-Admin-1`, dev only).

- `bash scripts/seed-gitea.sh` creates `aegis/secrets-demo` (TruffleHog fixtures) and
  `aegis/renovate-demo` (outdated dependencies), teaches the k3s cluster to resolve
  `gitea`, stores the Gitea token as `renovate-token`, registers both repositories and
  sets the URL mapping.
- `bash scripts/gitea-e2e.sh` runs the whole scenario and asserts against both AegisScan
  and Gitea: findings and masked previews, file/commit links, read-only Renovate opening
  no pull requests, PR mode opening real pull requests and the dashboard issue, links
  resolving, and a second run being idempotent.

## Per-provider tokens, webhooks, alerts and SLAs

- **Forge tokens:** PR mode picks `renovate-token-<provider>` (e.g. `renovate-token-gitea`,
  `-github`, `-gitlab`) from `aegisscan-scanner-credentials`, falling back to
  `renovate-token`. A repository whose provider has no token fails its run with a message
  naming the missing key, so a mixed estate can hold one token per forge.
- **Webhook:** Settings → Webhook URL + signing secret. Each request carries
  `X-Aegis-Signature: sha256=<HMAC-SHA256 of the body>` when a secret is set; the secret is
  write-only. *Send test event* posts `webhook.test` and shows the result (audited).
- **Alerts** are derived from live signals (blocked repositories, a scanner's latest run
  failed, open verified findings). Acknowledging or snoozing an alert is stored server-side
  and audited (`PUT/DELETE /api/v1/alerts/acks`), so it holds for every user.
- **Findings:** each has an optional assignee, a remediation due date (first seen + the SLA
  for its severity, default critical 7 / high 30 / medium 90 / low 180 days, setting
  `slaDays`), an overdue flag, and a history of triage decisions (state changes require a
  reason for *ignored* / *false positive*). `GET /api/v1/findings/stats` reports open,
  overdue and mean time to resolve (last 90 days).
- **Policies** (UI: Policies) lists the `ScanPolicy` resources (scanners, schedules, limits,
  status, repositories using them) read-only; they remain GitOps-managed.
- **Exports:** `GET /api/v1/reports/{findings,runs,audit}.csv` (audit is admin-only); cells
  that start with `= + - @` are neutralized against spreadsheet formula injection.
- **Repository ids** that contain `/` travel as `~` in URL paths (the UI does this).

## Enrichment of dependency findings

After a Renovate scan, each dependency finding is enriched best-effort (a failure never fails the scan):

- **Manifest line:** the manifest is fetched from the forge at the scanned commit and the
  dependency's line is stored, so file links open at it (Gitea/GitHub/GitLab raw URLs; private
  repositories that need credentials simply keep a file-level link).
- **Known vulnerabilities:** the dependency's current version is looked up in
  [OSV.dev](https://osv.dev) (npm, PyPI, Maven, Go, crates.io, NuGet, RubyGems, Packagist,
  GitHub Actions). Matches are shown with links and raise the finding to *high*. Responses are
  cached for 6 hours. Set the `vulnerabilityLookup` setting to `false` where outbound access to
  `api.osv.dev` is not allowed.
