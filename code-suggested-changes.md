# AegisScan — Suggested Code Changes

Concrete findings from reading the source. Ranked most severe first. All line references verified
against the working tree.

---

## S1 — Critical

### 1. Git workspace preparation performs a second network clone (violates the core invariant)

**`internal/database/gitcache.go:68`**

```go
if err := run(ctx, workspace, "git", "clone", "--branch", branch,
    "--reference", canonical, repo.CloneURL, filepath.Join(workspace, "repo")); err != nil {
```

`--reference` shares *objects* but `git clone <url>` still opens a network connection, negotiates
refs and authenticates against `repo.CloneURL` on **every** workspace preparation. The spec calls
"one repository clone, many tools" a fundamental requirement (§2.2) and closes with it as *the core
invariant*; Flow 3 (§23) explicitly states "No full network reclone should occur during the normal
path." `docs/architecture/git-cache.md` and `docs/adr/0001-shared-git-mirror.md` both claim the
current code prevents this. It does not.

Consequences beyond the spec: N repos × M scanners × K scans of provider API/bandwidth load, plus
every scan needs live credentials and fails during a provider outage even though the mirror is warm.

**Fix** — clone from the local mirror, never the remote:

```go
// Local path source: no network, no credentials, objects hardlinked.
if err := run(ctx, workspace, "git", "clone", "--shared", "--branch", branch,
    canonical, filepath.Join(workspace, "repo")); err != nil {
```

`--shared` hardlinks/borrows the mirror's object store. If a fully independent copy is needed, drop
`--shared` — a local-path clone still avoids the network. For TruffleHog (which wants full history)
`git clone --shared` from the mirror is the right primitive; `git worktree add` from the mirror is a
cheaper alternative but mutates the mirror's `worktrees/` metadata, so prefer `--shared`.

---

### 2. Argument/transport injection via `repo.CloneURL` into `git clone`

**`internal/database/gitcache.go:52` and `:68`**

```go
run(ctx, c.Root, "git", "clone", "--mirror", repo.CloneURL, canonical)
```

`CloneURL` flows in from the `Repository` CRD (`deploy/crds/aegisscan.io_repositories.yaml:23`,
no `pattern` constraint) and from provider discovery. Two live risks:

- **`ext::` transport ⇒ RCE.** `git clone 'ext::sh -c whoami'` makes Git execute an arbitrary
  command. A cluster user who can create a `Repository` CR gets code execution in the Aegis pod
  (which mounts the shared cache PVC and holds the ServiceAccount token).
- **Argument injection.** A URL beginning with `-` or `--` is parsed as a flag, e.g.
  `--upload-pack=<cmd>`.

`GIT_TERMINAL_PROMPT=0` (`gitcache.go:85`) prevents credential prompts but does nothing about either.

**Fix** — validate at the trust boundary and terminate option parsing:

```go
func validCloneURL(raw string) error {
    u, err := url.Parse(raw)
    if err != nil {
        return fmt.Errorf("parse clone URL: %w", err)
    }
    switch u.Scheme {
    case "https", "ssh":
    default:
        return fmt.Errorf("unsupported clone URL scheme %q", u.Scheme)
    }
    if u.Host == "" {
        return fmt.Errorf("clone URL host is required")
    }
    return nil
}
```

Call it in `Prepare` alongside the existing check at `:36`, and pass `"--"` before the URL:
`"git", "clone", "--mirror", "--", repo.CloneURL, canonical`. Also add
`protocol.ext.allow=never` to the command env, and a `pattern: '^(https|ssh)://'` to the CRD schema.
This is a security-scanning product; its own repository-acquisition path is its largest attack surface.

---

### 3. The default deployment serves the API with no authentication

**`cmd/aegisscan/main.go:18-25`** and **`internal/server/api.go:71-86`**

```go
handler := server.NewHandler(api)           // no auth middleware
if oidcIssuer != "" || ... {                // only then is auth installed
    handler = server.NewHandlerWithAuth(...)
}
```

```go
func role(r *http.Request) domain.Role {
    if principal, ok := PrincipalFromContext(r.Context()); ok { return principal.Role }
    if r.Context().Value(roleHeaderKey{}) != true { return domain.RoleViewer }   // ← anonymous ⇒ Viewer
```

With `oidc.enabled: false` (the Helm default, `values.yaml:43`) any unauthenticated caller that can
reach the Service is a Viewer and can read `/api/v1/repositories`, `/runs`, `/findings` and
`/settings`. Spec §10 requires server-side enforcement; "everyone is a Viewer" is not enforcement.

**Fix** — fail closed. Require an explicit opt-out:

```go
switch {
case oidcConfigured:
    handler = server.NewHandlerWithAuth(api, authenticator)
case os.Getenv("AEGIS_ALLOW_ANONYMOUS") == "true":
    log.Print("WARNING: anonymous read-only access enabled; do not use in production")
    handler = server.NewHandler(api)
default:
    log.Fatal("configure AEGIS_OIDC_* or set AEGIS_ALLOW_ANONYMOUS=true for local development")
}
```

and change `role()` to return `""` (no role, allows nothing) rather than `RoleViewer` when there is
no principal and the dev header is off.

---

### 4. GitHub discovery decodes `clone_url` and `default_branch` as empty strings

**`internal/discovery/provider.go:64-67`**

```go
var items []struct {
    Name, CloneURL, DefaultBranch string
    Archived                      bool
}
```

Go's JSON decoder matches field names case-insensitively but does **not** ignore underscores, so
GitHub's `clone_url` does not bind to `CloneURL`, nor `default_branch` to `DefaultBranch`. Verified
empirically: `[{Name:a CloneURL: DefaultBranch: Archived:false}]`.

Every discovered repository therefore carries an empty clone URL, which `gitcache.go:36` rejects
outright. `provider_test.go:26` only asserts `got[0].Name`, so the test passes while the feature
is dead.

Compounding it, **`provider.go:79`** never sets `domain.Repository.ID` — the primary key
(`migrations/001_initial.sql:2`) and the cache directory name (`gitcache.go:42`).

**Fix**:

```go
var items []struct {
    Name          string `json:"name"`
    FullName      string `json:"full_name"`
    CloneURL      string `json:"clone_url"`
    DefaultBranch string `json:"default_branch"`
    Archived      bool   `json:"archived"`
}
...
result = append(result, domain.Repository{
    ID:            domain.RepositoryID(item.FullName),   // "org/repo" — stable and unique
    Name:          item.FullName,
    Provider:      "github",
    CloneURL:      item.CloneURL,
    DefaultBranch: item.DefaultBranch,
    Status:        domain.RepositoryReady,
})
```

Extend `provider_test.go:26` to assert `CloneURL`, `DefaultBranch` and `ID` — the assertion gap is
what let this ship.

---

### 5. Execution IDs are not unique — every re-run collides on the primary key

**`internal/app/scan.go:36`**

```go
execution := domain.Execution{ID: string(repoID) + "-" + string(scanner), ...}
```

`scan_executions.id` is `TEXT PRIMARY KEY` (`migrations/001_initial.sql:17`) and
`Postgres.CreateExecution` (`postgres.go:94`) is a plain `INSERT` with no `ON CONFLICT`. The second
scan of any repo+scanner pair fails with a unique-violation at `scan.go:53`. It also destroys the
history the spec requires ("scan history survives Kubernetes Job deletion", §26) — one row per
repo+scanner forever, with no retry/attempt history.

Note the inconsistency: `MemoryState.StartRepositoryRun` (`state.go:53`) correctly uses
`fmt.Sprintf("%s-%s-%d", id, scanner, time.Now().UnixNano())`.

**Fix** — generate a unique ID per execution and keep the stable key as separate columns (they
already exist: `repository_id`, `scanner`, `attempt`):

```go
execution := domain.Execution{
    ID:           uuid.NewString(),   // or fmt.Sprintf("%s-%s-%d", repoID, scanner, now().UnixNano())
    RepositoryID: repoID, Scanner: scanner, Mode: mode,
    Status:       domain.ExecutionPreparing, Attempt: 1,
}
```

Add `CREATE INDEX scan_executions_repo_scanner_created_idx ON scan_executions (repository_id, scanner, created_at DESC);`
so history queries stay indexable.

---

## S2 — High

### 6. TruffleHog parser reads the wrong metadata key for the source it invokes

**`internal/plugins/trufflehog.go:29-36`** vs **`:47`**

```go
SourceMetadata struct {
    Data struct {
        Files struct {                       // ← filesystem source
            File string `json:"file"`
            Line int    `json:"line"`
        } `json:"Files"`
    } `json:"Data"`
} `json:"SourceMetadata"`
```

```go
args := []string{"git", "file://" + workspace.Path, "--json"}   // ← git source
```

TruffleHog's **git** source emits `SourceMetadata.Data.Git` with `commit`, `file`, `email`,
`repository` and `line`. `Files` is the filesystem source's key. Against the command actually run,
`raw.SourceMetadata.Data.Files.File` is always `""`, so:

- `Finding.SafePath` is always empty — the spec requires "file/path where safely available" (§13).
- `Finding.CommitSHA` is never populated at all (`trufflehog.go:107` omits it) — the spec requires
  a commit reference (§13).
- The fingerprint at `:100` degenerates to `sha256(detector + "\x00" + "" + "\x00" + raw)`, so the
  same secret in two different files collapses to one finding, and moving a file no longer changes
  the fingerprint. Dedup and finding lifecycle are both wrong.

`trufflehog_command_test.go:25` uses a `Files`-shaped fixture, so the test confirms the bug rather
than catching it.

**Fix** — decode the Git shape (keep `Files` too if a filesystem source is planned):

```go
SourceMetadata struct {
    Data struct {
        Git struct {
            Commit     string `json:"commit"`
            File       string `json:"file"`
            Line       int    `json:"line"`
            Repository string `json:"repository"`
        } `json:"Git"`
    } `json:"Data"`
} `json:"SourceMetadata"`
```

and set `CommitSHA: raw.SourceMetadata.Data.Git.Commit` on the `domain.Finding` at `:107`. Replace
the test fixture with real `trufflehog git --json` output.

---

### 7. `--branch` is given a commit SHA on incremental scans

**`internal/plugins/trufflehog.go:52`**

```go
args = append(args, "--since-commit", workspace.CommitFrom, "--branch", workspace.Commit)
```

`workspace.Commit` is assigned `execution.CommitTo` (`app/scan.go:65`), which
`domain.CommitRange` (`checkpoint.go:29`) returns as `repo.LatestCommitSHA` — a SHA.
`--branch` expects a branch name; TruffleHog will fail or scan nothing.

**Fix** — pass the repository's default branch (already available on `domain.Repository`) through
`app.Workspace`, and use `Commit`/`CommitFrom` only for `--since-commit`:

```go
args = append(args, "--since-commit", workspace.CommitFrom)
if workspace.Branch != "" {
    args = append(args, "--branch", workspace.Branch)
}
```

Add `Branch string` to `app.Workspace` (`internal/app/ports.go:38-43`) and populate it in
`GitCache.Prepare`, which already computes it at `gitcache.go:64-67`.

---

### 8. API serializes domain structs with no JSON tags — the frontend already reads names the server never emits

**`internal/domain/repository.go:15-27`, `:56-70`; `internal/domain/finding.go:13`;
`internal/domain/authorization.go:34`** — none of these have JSON tags. They are written straight to
the wire by `writeJSON` (`api.go:87-91`), producing Go PascalCase:

```json
[{"ID":"repo-1","Name":"platform/api","ActiveFindingCount":3, ...}]
```

**`web/src/dataClient.js:6`** reads:

```js
repository.name || repository.Name          // works via the fallback
repository.activeFindingCount ?? 0          // always undefined ⇒ always renders 0
```

So the findings column is permanently `0` the moment the UI is pointed at a real API. This is also
the §16 violation "stable DTOs separate from database models" — today the domain model *is* the
public API contract, so any internal rename is a breaking API change.

**Fix** — add an `internal/server/dto` package (the spec prescribes it at §17) with explicit
tagged types and mapping functions:

```go
package dto

type Repository struct {
    ID                 string `json:"id"`
    Name               string `json:"name"`
    Provider           string `json:"provider"`
    Status             string `json:"status"`
    CacheState         string `json:"cacheState"`
    LatestCommitSHA    string `json:"latestCommitSha"`
    ActiveFindingCount int    `json:"activeFindingCount"`
}

func FromRepository(r domain.Repository) Repository { ... }
```

Minimum viable alternative if the DTO layer is deferred: add `json:"…"` tags to the domain structs
and fix `dataClient.js`. But the DTO layer is what the spec asks for and it is cheap here.

---

### 9. Audit events have no timestamp

**`internal/domain/authorization.go:34-41`**

```go
type AuditEvent struct {
    Actor    string
    Role     Role
    Action   string
    Target   string
    Outcome  string
    Metadata map[string]string
}
```

Spec §11 requires capturing actor, action, target, **timestamp**, outcome and metadata. The DB
column exists (`migrations/001_initial.sql:63` `created_at`) but the struct cannot carry it, so
`Postgres.AppendAudit` (`postgres.go:134`) never selects it back and `/api/v1/audit` (`api.go:278`)
returns undated events. An undated audit trail has no forensic value.

Separately, **`api.go:275`** constructs every event with `Metadata` nil — the spec's "relevant
non-secret metadata" (scan mode, scanner, previous/new setting values) is never recorded.

**Fix**:

```go
type AuditEvent struct {
    Timestamp time.Time         `json:"timestamp"`
    Actor     string            `json:"actor"`
    Role      Role              `json:"role"`
    Action    string            `json:"action"`
    Target    string            `json:"target"`
    Outcome   string            `json:"outcome"`
    Metadata  map[string]string `json:"metadata,omitempty"`
}
```

Set it in `recordAudit` and add a `metadata map[string]string` parameter so `runs`/`settings`
callers can pass scanner+mode and the changed setting keys.

---

### 10. Executions never record start or finish time

**`internal/app/scan.go:36-112`** — `domain.Execution.StartedAt` and `FinishedAt`
(`repository.go:65-66`) are never assigned anywhere in the service. `MemoryState.StartRepositoryRun`
(`state.go:53`) also omits them.

The spec requires "timestamps and duration" in PostgreSQL (§4), "start/end/duration" on the
execution detail view (§13), an execution-duration metric (§20) and a "Scan duration p50/p95"
dashboard chart (§13). None are computable.

**Fix** — in `ScanService.Run`:

```go
started := now()
execution.StartedAt = &started
// ... after plugin.Run returns:
finished := now()
execution.FinishedAt = &finished
```

before the final `Executions.Update` at `:109`.

---

## S3 — Medium

### 11. Migration runner is not a migration system

**`internal/database/migrate.go:9-20`** executes one SQL string. There is no schema-version table,
no ordered discovery of `migrations/*.sql`, no down migrations, no advisory lock (so two replicas
racing on startup both apply DDL), and nothing in `cmd/aegisscan/main.go` calls it.

Spec §15: "Use a migration system appropriate for Go and commit all migrations."

**Fix** — use `golang-migrate/migrate` or `pressly/goose` with `embed.FS`, run on startup behind
`pg_advisory_lock`. That is a few lines and removes a whole class of upgrade failures. If a
dependency is unwanted, at minimum add a `schema_migrations(version)` table, sort files by prefix,
apply each in its own transaction, and wrap the run in `SELECT pg_advisory_lock(...)`.

---

### 12. Pagination loads every row before slicing

**`internal/server/api.go:107-118`** + **`internal/database/postgres.go:27,47,73`**

```go
writeJSON(w, http.StatusOK, paginate(a.Executions.ListExecutions(), r))
```

`ListExecutions` is `SELECT ... FROM scan_executions ORDER BY created_at DESC` with no `LIMIT`.
Every request materializes the entire table into memory, then discards all but ≤100 rows. Spec §15:
"All high-volume dashboard queries must be indexable and paginated."

The response is also a bare JSON array with no total or next cursor, so a client cannot page —
`web/src/dataClient.js` has no pagination at all.

**Fix** — push limit/offset into SQL and return an envelope:

```go
func (p Postgres) ListExecutions(ctx context.Context, limit, offset int) ([]domain.Execution, int, error) {
    // SELECT ... ORDER BY created_at DESC LIMIT $1 OFFSET $2
    // plus SELECT count(*) FROM scan_executions
}
```

```json
{ "items": [...], "page": 1, "pageSize": 50, "total": 1284 }
```

Also drop the unused `values int` parameter on `page()` (`api.go:93`).

---

### 13. `SweepStale` re-cancels the same executions forever and can panic

**`internal/app/scheduler.go:135-146`**

```go
for id, started := range s.started {
    if now.Sub(started) >= s.StaleAfter {
        s.cancel[id]()          // ← nil-map-value call if the maps ever diverge
        stale = append(stale, id)
    }
}
```

Two problems:
1. `s.started[id]` is never deleted here, so every subsequent sweep re-reports and re-cancels the
   same ID until the worker goroutine's own `defer` (`:59`) removes it. A caller that logs or emits
   metrics on the returned slice will double-count.
2. `s.cancel[id]` is read without an `ok` check. The two maps are maintained together today, but
   any future divergence turns a housekeeping call into a nil-func panic that takes down the process.

**Fix**:

```go
for id, started := range s.started {
    if now.Sub(started) < s.StaleAfter {
        continue
    }
    if cancel, ok := s.cancel[id]; ok {
        cancel()
    }
    delete(s.started, id)
    stale = append(stale, id)
}
```

(Leave `s.cancel[id]` for the worker's own defer to clean up, so a late-returning worker still
finds its entry.)

---

### 14. `Release` deletes the parent of whatever path it is handed

**`internal/database/gitcache.go:75-80`**

```go
func (c *GitCache) Release(_ context.Context, workspace app.Workspace) error {
    if workspace.Path == "" { return nil }
    return os.RemoveAll(filepath.Dir(workspace.Path))
}
```

`Workspace` is a plain struct that any caller can construct. `Release` performs an unconditional
recursive delete of `Dir(Path)` with no check that the path is inside the cache root or was ever
produced by `Prepare`. A `Workspace{Path: "/var/lib/aegisscan/git-cache/repo-1.git"}` would delete
the entire cache root.

**Fix** — record the workspace root and validate containment:

```go
func (c *GitCache) Release(_ context.Context, workspace app.Workspace) error {
    if workspace.Path == "" {
        return nil
    }
    root := filepath.Dir(workspace.Path)
    base := filepath.Base(root)
    if !strings.HasPrefix(base, "aegisscan-workspace-") {
        return fmt.Errorf("refusing to remove unmanaged workspace %q", root)
    }
    return os.RemoveAll(root)
}
```

Better still, add a `Root string` field to `app.Workspace` set by `Prepare`, and delete that.

---

### 15. Checkpoint load errors are silently ignored for full scans

**`internal/app/scan.go:40-44`**

```go
checkpoint, checkpointErr := s.Checkpoints.Get(ctx, repoID, scanner)
if checkpointErr != nil && mode == domain.ScanIncremental {
    return execution, fmt.Errorf("load scan checkpoint: %w", checkpointErr)
}
```

For a full scan, a database connection failure, timeout or permission error is swallowed and the
zero-value checkpoint is used. That is correct for `sql.ErrNoRows` and wrong for everything else —
it hides a broken database behind apparently-successful scans.

**Fix**:

```go
checkpoint, checkpointErr := s.Checkpoints.Get(ctx, repoID, scanner)
switch {
case errors.Is(checkpointErr, sql.ErrNoRows):
    // no checkpoint yet — fine for a full scan, CommitRange rejects it for incremental
case checkpointErr != nil:
    return execution, fmt.Errorf("load scan checkpoint: %w", checkpointErr)
}
```

`sql` in the domain/app layer is undesirable; define `var ErrCheckpointNotFound = errors.New(...)`
in `internal/domain` and have the Postgres adapter translate `sql.ErrNoRows` into it.

---

### 16. `internal/database.CheckpointStore` does not satisfy `app.CheckpointStore`

**`internal/database/checkpoints.go:22`** returns `(domain.ScanCheckpoint, bool)`;
**`internal/app/ports.go:25`** requires `(domain.ScanCheckpoint, error)`. Meanwhile
**`internal/database/postgres.go:119`** names its methods `GetCheckpoint`/`SaveCheckpoint`, which
also do not match the interface's `Get`/`Save`.

Nothing catches this because nothing wires them together. The same divergence exists for
`ExecutionStore` (`Create`/`Update` vs `CreateExecution`/`UpdateExecution`) and `FindingStore`
(`Save` vs `SaveFinding`).

**Fix** — add compile-time assertions next to each adapter so the mismatch is a build error the
moment someone tries to wire it:

```go
var (
    _ app.CheckpointStore = (*CheckpointStore)(nil)
    _ app.ExecutionStore  = Postgres{}
    _ app.FindingStore    = Postgres{}
    _ app.CheckpointStore = Postgres{}
)
```

`gitcache.go:92` already does exactly this (`var _ app.GitCache = (*GitCache)(nil)`) — apply the
same discipline to the rest of the package. This will fail to compile today, which is the point.

---

### 17. Renovate log sanitizer is trivially bypassed and is the only sanitizer

**`internal/plugins/renovate.go:53-62`**

```go
if strings.Contains(lower, "token") || strings.Contains(lower, "password") || strings.Contains(lower, "secret") {
    lines[i] = "[redacted scanner output]"
}
```

Line-oriented keyword matching misses: JSON output on a single line (redacting the whole run),
base64/URL-encoded credentials, `Authorization: Bearer …`, `ghp_…`/`github_pat_…` tokens,
`https://user:pass@host` URLs, private keys, and any multi-line secret. It also over-redacts —
one occurrence of the word "token" destroys the entire log line the operator needs.

Worse: **TruffleHog output is never sanitized at all.** `trufflehog.go:72` returns
`app.ScanResult{Findings: …, Summary: …}` with `Log` unset, so the raw JSONL containing every
discovered secret in `Raw` is simply discarded rather than sanitized — which is safe today, but
means there is no reviewable TruffleHog log, and any future change that sets `Log: string(output)`
would persist plaintext secrets straight into `scan_executions.sanitized_log`.

**Fix** — one shared sanitizer in a `plugins` or `telemetry` helper, applied to *all* scanner
output before it reaches `ScanResult.Log`, with pattern-based redaction that preserves context:

```go
var secretPatterns = []*regexp.Regexp{
    regexp.MustCompile(`(?i)(authorization|api[-_]?key|token|password|secret)\s*[:=]\s*\S+`),
    regexp.MustCompile(`gh[pousr]_[A-Za-z0-9]{20,}`),
    regexp.MustCompile(`github_pat_[A-Za-z0-9_]{20,}`),
    regexp.MustCompile(`://[^/\s:]+:[^/\s@]+@`),           // credentials in URLs
    regexp.MustCompile(`-----BEGIN [A-Z ]*PRIVATE KEY-----`),
}

func Sanitize(value string) string {
    for _, p := range secretPatterns {
        value = p.ReplaceAllString(value, "[REDACTED]")
    }
    return value
}
```

Add a unit test with a fixture containing each pattern. Also add a hard cap on log length before
persisting (`scan_executions.sanitized_log` is unbounded `TEXT`).

---

## S4 — Lower severity / maintenance

### 18. No HTTP server timeouts — trivially Slowloris-able

**`cmd/aegisscan/main.go:27`** — `http.ListenAndServe(address, handler)` uses the zero-value
`http.Server`: no `ReadHeaderTimeout`, `ReadTimeout`, `WriteTimeout` or `IdleTimeout`. Standard
`gosec` G114.

```go
srv := &http.Server{
    Addr:              address,
    Handler:           handler,
    ReadHeaderTimeout: 10 * time.Second,
    ReadTimeout:       30 * time.Second,
    WriteTimeout:      60 * time.Second,
    IdleTimeout:       120 * time.Second,
}
```

### 19. No graceful shutdown

**`cmd/aegisscan/main.go:27-29`** — no `signal.NotifyContext` / `srv.Shutdown`. On rollout, in-flight
requests are cut and (once scanning is wired) running scans lose their state transition. Add:

```go
ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
defer stop()
go func() { <-ctx.Done(); _ = srv.Shutdown(context.WithTimeout(context.Background(), 20*time.Second)) }()
```

### 20. JWKS is re-fetched on every single request

**`internal/server/auth.go:54-71`** — `RemoteOIDCVerifier.Verify` calls `fetchKeys` per request. Every
authenticated API call makes an outbound HTTPS round-trip to the IdP. The comment at `:43-46`
acknowledges this. Beyond latency, it makes the API's availability equal to the IdP's and is an
easy amplification vector (unauthenticated requests with a bogus `Bearer` still trigger the fetch —
actually they do, since `fetchKeys` runs before parsing).

**Fix** — cache keys with a TTL and refresh-on-unknown-`kid`:

```go
type RemoteOIDCVerifier struct {
    ...
    mu        sync.RWMutex
    keys      map[string]*rsa.PublicKey
    fetchedAt time.Time
    TTL       time.Duration // default 15m
}
```

Refresh when `time.Since(fetchedAt) > TTL`, or once (rate-limited) when a token presents an unknown
`kid`. Also set a timeout on the HTTP client at `:85` — `http.DefaultClient` has none.

### 21. Two different error formats

**`internal/server/api.go`** returns `{"code":"..."}` with JSON content-type; **`auth.go:228,233`**
returns `http.Error(w, "missing bearer token", 401)` — plain text. Spec §16 requires a consistent
error format. Route the auth failures through `writeJSON(w, 401, map[string]string{"code":"unauthenticated"})`.

### 22. No request body size limit

**`internal/server/api.go:157,252`** — `json.NewDecoder(r.Body).Decode(...)` with no
`http.MaxBytesReader`. An unauthenticated POST (see finding 3) with a multi-GB body is an easy OOM.

```go
r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
```

Apply in `newHandler` (`health.go:33`) so it covers every route. Also reject unknown fields with
`decoder.DisallowUnknownFields()` on the settings PUT so typos surface instead of silently
zeroing a field.

### 23. Settings PUT does a full replace with no zero-value guard

**`internal/server/api.go:251-259`** decodes into a fresh `domain.RuntimeSettings{}`, so any field
the client omits becomes the zero value. `Validate()` (`domain/settings.go:28`) catches
`MaxConcurrency`, `RetentionDays`, `CacheRetentionGB` and `DefaultScanMode`, but **not**
`FullScanSchedule`, `TruffleHogImage` or `RenovateImage` — a PUT that omits those silently wipes the
configured scanner images. Read-modify-write, or validate that the image fields are non-empty.

### 24. NetworkPolicy egress allows all destinations

**`deploy/helm/aegisscan/templates/networkpolicy.yaml:8`**

```yaml
egress: [{ to: [], ports: [{ protocol: TCP, port: 443 }, { protocol: TCP, port: 5432 }, { protocol: UDP, port: 53 }] }]
```

`to: []` means "any destination". Port 5432 to the entire cluster and internet is broader than
needed. Scope PostgreSQL to a namespace/pod selector and DNS to `kube-system`:

```yaml
egress:
  - to: [{ namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: kube-system } } }]
    ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
  - to: [{ podSelector: { matchLabels: { app.kubernetes.io/name: postgresql } } }]
    ports: [{ protocol: TCP, port: 5432 }]
  - ports: [{ protocol: TCP, port: 443 }]   # Git providers, IdP, K8s API
```

Also note SSH (22) is blocked, so `ssh://` clone URLs will fail once cloning is wired.

### 25. `postgresql.*` Helm values are referenced by no template

**`deploy/helm/aegisscan/values.yaml:23-28`** declares `host`, `port`, `database`, `existingSecret`.
`grep -rn postgres deploy/helm/aegisscan/templates/` returns nothing. Operators will set these and
observe no effect. Either wire them into `deployment.yaml` env (with the secret projected via
`secretKeyRef`) or delete them.

### 26. `containerSecurityContext` is missing standard hardening

**`deploy/helm/aegisscan/values.yaml:31-34`** sets `runAsUser/runAsGroup/readOnlyRootFilesystem`
but omits `allowPrivilegeEscalation: false`, `capabilities: {drop: [ALL]}` and
`seccompProfile: {type: RuntimeDefault}`. Without those the pod fails `restricted` Pod Security
Admission. Also: `readOnlyRootFilesystem: true` with no writable volume means
`os.MkdirTemp("")` (`gitcache.go:60`) will fail once the cache is wired — add an `emptyDir` at `/tmp`
and set the workspace parent to the cache PVC instead.

### 27. Dead code

| Location | What |
|---|---|
| `internal/server/api.go:286` | `var _ = app.SettingsStore{}` — an import-anchor for a package the file otherwise doesn't use. `internal/app.SettingsStore` (`app/settings.go:10`) is itself never constructed; `MemoryState` (`state.go:151,157`) duplicates it. Delete one. |
| `internal/database/postgres.go:138-143` | `NullableTime` — never called. |
| `internal/database/postgres.go:149` | `var _ = sql.ErrNoRows` — another import anchor. |
| `internal/app/ports.go:59` | `ScanResult.Output fs.FS` — no producer, no consumer, forces an `io/fs` import. |
| `internal/plugins/renovate.go:64-68` | `ReaderRunner` — used only by `renovate_test.go`; move it to the test file. |
| `internal/plugins/renovate.go:18-22` | `RenovateSummary` is constructed at `:36` and immediately flattened into a `map[string]int` at `:50`; the type never escapes. |
| `internal/server/api.go:93` | `page(values int, ...)` — `values` is unused. |
| `internal/server/auth.go:18-28` | `OIDCConfig` + `Validate` — never referenced; `main.go:19-24` re-implements the check inline. |
| Repo root | `output/helm-*.yaml` (3 rendered Helm outputs), `node-registry.json` (126 KB), two 300 KB+ PNGs, `web/.playwright-cli/` logs — all committed build artifacts. Add to `.gitignore`. |
| Repo root | `task.md` and `tasks.md` are two overlapping task lists. Keep one. |

### 28. CI does not run the frontend tests

**`.github/workflows/ci.yml:15`** runs `npm run build` only. `npm run test` (Vitest) and
`npm run test:e2e` (Playwright) never execute in CI, so the 4 frontend test files and 3 e2e specs
provide no regression protection. Add:

```yaml
      - run: npm run test
        working-directory: web
```

There is also no `golangci-lint`, no `go build` of the container image, no `helm lint`/
`helm template` validation, and no `govulncheck` — despite `output/helm-*-rendered.yaml` showing
that Helm rendering was validated manually at some point.
