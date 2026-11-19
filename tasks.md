# AegisScan end-to-end review: fix & enhancement tasks

Source: live end-to-end test on 2026-10-13 against the compose stack (k3s v1.31.5, Keycloak, Postgres), TruffleHog and Renovate scans of real public repos (`trufflesecurity/test_keys`, `expressjs/express`), and a Playwright walk through every UI view (screenshots in `screenshots/`). The prior enterprise-readiness list is preserved in `docs/tasks-enterprise-readiness-2026-09.md`.

Legend: `[ ]` open · `[x]` done. Severity: **P0** wrong/misleading data or data loss, **P1** missing core capability, **P2** polish/hardening.

## What was verified working
- Compose stack boots; login via Keycloak (admin) works; RBAC-gated nav renders.
- Queueing TruffleHog + Renovate runs creates one k8s Job/Pod each (`aegisscan-<execution>-<attempt>`): `clone` init container then `scan` container; pods reach `Completed`.
- Retry with backoff works: attempts 1–2 failed (cluster DNS not ready after restart) and attempt 3 succeeded automatically.
- TruffleHog findings parsed and persisted (verified AWS/URI keys, PrivateKey); verified findings flip the repo to `Blocked`.
- Findings, repositories, runs, audit, settings and scanner catalog APIs return data.

## P0: wrong or misleading data

- [x] **T1. Renovate never produces dependency updates.** Under `--platform=local` Renovate creates no branches, so `report.branches` is always empty and `RenovateResultParser` finds nothing. Real data is in the `packageFiles with updates` debug log line (verified: express shows `eslint 8.47.0 -> 10.12.0`, `cookie ^0.7.1 -> ^2.0.0`, ...). Run Renovate with `LOG_LEVEL=debug` and parse that message; emit one `RenovateDependency` finding per dependency with a non-empty `updates[]`, carrying update type (major/minor/patch).
- [x] **T2. Renovate environment noise is reported as security findings.** "local platform does not support local presets", "GitHub token is required for some dependencies" become `RenovateConfig` findings (duplicated). Treat them as scan warnings in the run log; keep only genuine repo-config validation errors as findings, de-duplicated. Surface `github-token-required` skips as a warning and let operators supply `GITHUB_COM_TOKEN` to the scanner pod via a Kubernetes Secret.
- [x] **T3. Settings form shows defaults, and Save overwrites stored values.** `useState({...defaults, ...settings})` runs before settings load (UI showed retention 90 / cache 100; API holds 42 / 20). Sync the form when settings arrive.
- [x] **T4. `activeFindingCount` is always 0** (repository list, dashboard "needing attention", even for a Blocked repo). Compute from open findings.
- [x] **T5. Run "logs" are a one-line summary mislabelled "Kubernetes logs".** Persist a truncated, sanitized copy of the real scan container output (and the clone init-container error on failure) and serve it from `/runs/{id}/logs`.
- [x] **T6. Severity is invented in the browser and inconsistent** (unverified PrivateKey is "High" on the dashboard, "Medium" on Alerts). Compute severity server-side once (verified secret = critical; unverified secret = high; major dependency update = medium; minor/patch/config = low) and return it in the finding.
- [x] **T7. Runs are ordered by GUID, not time**; times show "—" for older runs. Order by `CreatedAt` desc and return `createdAt`.
- [x] **T8. Finding location leaks the pod workspace path** (`/workspace/repo/keys`). Strip the prefix to a repo-relative path.
- [x] **T9. Audit log Time column always "—"** although the API returns `timestamp`; metadata not shown.
- [x] **T10. Findings have no first/last seen, commit, scanner or source execution** (UI shows "Not available" / "—"). Add `scanner`, `firstSeenAt`, `lastSeenAt`, `executionId`; the commit comes from the cloned HEAD (`git rev-parse` in the clone step).
- [x] **T11. Generic remediation text** ("Rotate the exposed credential...") is shown for Renovate findings too. Make it detector-specific.

## P1: missing core capability

- [x] **T12. No finding triage.** Add `PATCH /findings/{fingerprint}` (open/resolved/ignored/false_positive, audited, operator role) and UI controls. A rescan must not re-open a triaged finding.
- [x] **T13. Findings never close.** A successful rescan of the same repo+scanner should mark findings no longer detected as `resolved`.
- [x] **T14. `POST /runs` doesn't validate** that the repository/scanner exist (accepted, fails later) and `DELETE /runs/{id}` overwrites finished runs with `canceled`. Validate (400/404) and only cancel queued/running runs (409 otherwise).
- [x] **T15. Settings are decorative.** `maxConcurrency` was never enforced; `{{repository}}`/`{{branch}}`/`{{mode}}` placeholders were advertised but only `{{workspace}}` substituted; `defaultScanMode` ignored. Done: concurrency enforced in the worker, all placeholders substituted, default mode applied. (Retention sweep already existed in `SchedulingWorker`; the unenforced cache-retention and full-scan-schedule fields were removed from the UI.)
- [x] **T16. Deleting a repository leaves its findings/executions orphaned.** Cascade, via one shared `RemoveRepositoryWithData` used by both the API delete and the CRD-delete reconcile path.
- [x] **T17. Alerts view re-lists every finding** (unpaginated, duplicates the Findings page, no acknowledge). Now limited to blocked repos, scanners whose latest run failed, and open verified findings, shown 25 at a time ("Show more"). Acknowledging an alert is done by triaging the finding / unblocking the repo.
- [x] **T18. Runs table lacks duration, attempt, finding count; Renovate/TruffleHog views don't show what was found.** Add them, with a link to the run's findings.
- [x] **T19. Repositories view:** no per-row "Scan now", "Remove" confirmed via `window.confirm` but then reloaded the whole page, and sat inside the status pill, cache column/state is seed-data fiction, sidebar "Git cache: Ready" is hard-coded.
- [x] **T20. Findings view promises commit/remediation but can't filter by state/verified/scanner.** Added state/scanner/text filters (fingerprint searchable) in the UI over the 200-row page the loaders fetch; the API also supports `scanner`, `detector`, `state`, `verified`, `repositoryId` filters and paging for future server-side paging.

## P2: hardening & polish

- [x] **T21. Scan pods are unhardened.** Add `securityContext` (runAsNonRoot where the image allows, no privilege escalation, drop ALL caps, seccomp RuntimeDefault), resource requests/limits, `activeDeadlineSeconds`, `automountServiceAccountToken: false`. Pin `renovate/renovate` (currently `:latest`).
- [x] **T22. k3s compose service has no stable node name**; every container recreate adds a stale `NotReady` node. Set `hostname`.
- [x] **T23. Log volume:** EF Core/ASP.NET log every SQL command and request at Information. Raise to Warning by default.
- [x] **T24. Built-in scanners are labelled "Custom" with a Remove button** the API rejects (400). Show "Built-in" and hide Remove.
- [x] **T25. System view is shallow** (version hard-coded). Report Kubernetes API reachability and queue depth.
- [x] **T26. Header sign-out is an unlabeled "X"; page-transition fade makes screenshots/automation flaky** (content opacity animates in on every navigation).
- [x] **T27. Tests:** parser/sanitizer/severity/Job-spec/scanner-id unit tests (40 backend tests), client tests for the new contract, and a Playwright e2e for the triage flow (operator ignore -> reopen, viewer cannot triage), run in both fixture and compose mode. (HTTP-level API tests for validation/triage were verified live with curl/UI rather than added as WebApplicationFactory tests; the repo has no API test harness yet.)

## Additional defects found while implementing (all fixed)
- [x] **T28. Webhook notifications could never fire from the UI.** The Alerts UI saved `webhookURL` while the backend reads `webhookUrl`, and the inline Alerts branch in `main.tsx` shadowed `AlertsView`, so the webhook form never rendered. Removed the shadowing branch and aligned the key.
- [x] **T29. Role mappings were saved to the wrong place.** The Roles UI wrote into the settings blob; the backend only honours `/api/v1/role-mappings`. Wired the view to that API (verified through the UI).
- [x] **T30. Dashboard/alert counts were silently capped at 50 rows** (data loaders used the API's default page). Loaders now request the maximum page (200). Beyond 200 rows needs server-side aggregate endpoints.
- [x] **T31. Audit time root cause:** UI read `createdAt`, API sends `timestamp`.
- [x] **T32. Policies view had the same stale-defaults bug as Settings** (checkbox showed the default, not the stored value). Both forms now remount when stored settings change.

## Verification summary (2026-10-13)
- Live: TruffleHog + Renovate Jobs on `test-keys-e2e` and `express-e2e` ran as hardened pods (non-root, read-only rootfs, dropped caps, resource limits) and completed; Renovate found 78 dependencies and 11 updates on express; ignored findings survive a rescan; fixed findings auto-resolve only on authoritative runs.
- `dotnet test`: 101 passed. `npm run test` (vitest): all passed. `npm run typecheck`: clean. `npm run test:e2e` (fixture) and `npm run test:e2e:compose`: all passed (incl. axe accessibility and the triage flow).
- Screenshots: `screenshots/NN-*.png` (before) and `screenshots/after-*.png` (after).

## Known limits / follow-ups
- Fresh k3s clusters take ~1 min for cluster DNS to settle; scans started in that window fail their first attempts and recover through the retry/backoff path (the real clone error is now visible in the run output).
- Renovate cannot look up GitHub-hosted dependencies (GitHub Actions) without a token: create the Secret `aegisscan-scanner-credentials` (key `github-token`); until then those are reported as skipped in the run output.
- Existing databases get the new Renovate image/args via migration `ScanFindingDetails` (overwrites local edits to the built-in Renovate scanner row).
- The unverified TruffleHog `Github` detector fires on SHA-pinned action refs in workflow files (medium severity); triage as false positive or tune later.

## Review follow-ups (second pass)
- [x] **T33. Auto-resolve could close real secret findings on an empty/truncated log.** TruffleHog results are authoritative only if the `finished scanning` line is present; Renovate dependencies skipped (e.g. expired token) are kept open. Unit-tested.
- [x] **T34. Scanner ids are validated** (they become a Kubernetes label value): lowercase alphanumerics and `-`. Verified live with a custom `alpine` scanner (placeholders, non-root, read-only rootfs, no token leak).
- [x] **T35. `GITHUB_COM_TOKEN` is mounted only into the `renovate` scanner**, not arbitrary admin-supplied images.
- [x] **T36. Scan mode was a no-op.** `full` now clones the complete history (`incremental` stays `--depth 50`); the dialog says so; the scan dialog defaults to the `defaultScanMode` setting. Still open: scanning git history (`trufflehog git`) so a finding's commit is the one that introduced it, not HEAD.
- [x] **T37. Scanner editor saved on every keystroke** (PUT + audit row each, risking the rate limit and half-typed images running). Edits are now a local draft saved by the button.
- [x] **T38. Docs described a Git cache that does not exist** (`AEGIS_GIT_CACHE_PVC`); `{{repository}}` now matches its documented meaning (repository id). `docs/operations/scanners.md` rewritten to match the implementation.

### Still open (not done)
- Git-history scanning for per-finding introducing commits (T36 remainder) and the ADR 0001 shared git cache.
- Server-side aggregate endpoints so dashboard counts are exact beyond 200 rows (T30 remainder).
- WebApplicationFactory-based HTTP API tests.
- CI also publishes with `PublishAot=true`; that was not run here (needs a native toolchain on this machine). The new code uses only source-generated regex and the same `System.Text.Json`/anonymous-type patterns already in the project.

## Gitea end-to-end + UI review pass (third pass)
- [x] **T39. Gitea in the compose stack** (`gitea` service, dev only) with `scripts/seed-gitea.sh` and the assertion script `scripts/gitea-e2e.sh`; scan pods resolve `gitea` via a CoreDNS custom entry. Verified: TruffleHog and Renovate clone from Gitea, 8 real pull requests + a Dependency Dashboard issue are created and linked, a second run is idempotent.
- [x] **T40. Renovate pull request mode** (Policies toggle): runs against the forge with a Secret-held token (operator pre-checks the key; Helm RBAC limited to that Secret by name), parses PRs/issue from the report, attaches the PR to each finding. Closed PRs stop being linked on forge-connected runs.
- [x] **T41. Forge-aware links built server-side** (`ForgeLinks`: GitHub/GitLab/Gitea; repo, commit, file#Lline, PR, issue) with an admin "Forge URL mapping" for internal vs public URLs; plain-http clone URLs only behind a dev flag.
- [x] **T42. Exact-issue preview.** TruffleHog findings show a masked match. Found and fixed while testing: TruffleHog's own `Redacted` field leaks key material (private-key lines) and whole AWS key ids, so previews are re-masked before storage and unit-tested.
- [x] **T43. UI navigation & information pass** (independent opus-model review + own critique): clickable KPI cards, repo names open the in-app report with a separate forge icon, breadcrumbs, active sub-nav, header search opens Repositories, findings drill-down chips (from runs, repos, dashboard), finding detail with preview + "Open file in <forge>" + commit + PR + "view the run that found it" + copy fingerprint, runs show commit/findings/PR chips and "succeeded after N retries", repo page with findings list, last scan, blocked reason and admin Unblock, bulk triage, CSV export (formula-injection-safe), date range only on the dashboard, no duplicated Run buttons.
- [x] **T44. CSV export formula injection** neutralized (finding fields come from repository content).

### Review follow-ups completed (fourth pass)
- [x] **T45. Settings validation.** `PUT /settings` rejects malformed values (types/ranges, `forgeUrlOverrides` must map absolute http(s) URLs); list endpoints no longer 500 on a bad legacy value. Unit-tested.
- [x] **T46. Triage needs a reason** for ignore/false-positive (400 without one), stored in the audit trail; **per-finding history** (`GET /findings/{fp}/history`) shown in the finding detail; bulk triage takes one reason.
- [x] **T47. Finding age** ("first seen ..." under every row), **Changelog / releases** link for Renovate findings (Renovate `sourceUrl`, migration `FindingSourceUrl`).
- [x] **T48. Repository health from the last finished scan** (Failing / Never scanned / Blocked / Ready) replaces the always-"Ready" status in lists and the repo page; `Status`/Blocked semantics unchanged.
- [x] **T49. Findings drill-down persists in the URL** (`findingsRepo`, `findingsRun`) so views can be shared/bookmarked; read-only roles see an explanation instead of silently missing triage buttons.
- [x] **T50. Gitea IP pinned** (`10.250.250.50` on a defined compose subnet) so the CoreDNS entry cannot go stale. Note: first start on a freshly created network can race k3s ("no default routes"); `docker compose up -d` again resolves it.
- [x] **T51. Links verified in a real browser** (file at the exact commit with the line highlighted, PR page) in addition to HTTP checks. (Removal of a repository already asks for confirmation via `window.confirm`; the earlier "no confirm" note was wrong.)

- [x] **T52. Independent opus-model UI reviews (3 rounds).** Round 1 found 15 gaps (most fixed above); round 2 blocked on a placeholder repo page on direct URLs, non-actionable alerts and mid-animation dashboard totals (fixed: loading/not-found states, alerts with timestamps + specific targets + inventory filter, reduced-motion aware counters, failure cause on failed runs); round 3 blocked on the dashboard "Active findings" figure (now inventory-scoped, split into secrets vs dependency updates) and on secret remediation copy that over-promised (filesystem rescans cannot verify git history; resolved-by-absence now says so; "last seen at commit" is updated each scan). A fourth review was not run; the reviewer stated these two fixes would move it to sign-off, and they were verified by screenshot and tests.
- Reviewer non-blockers still open: PR mode uses one global switch/token (no per-provider scope); alerts have no acknowledge/snooze/owner and the webhook lives on the Alerts page without test-send/signing; repositories table lacks pagination/sort/status filter; repo-page finding click opens the filtered list instead of that finding; Renovate `package.json` link has no line anchor.

### Fifth pass — the previously open items, implemented and verified (`scripts/gitea-e2e.sh` + Playwright)
- [x] **T53. Git-history scanning.** Full scans run `trufflehog git` on a full clone: deleted secrets are still found, the introducing commit is recorded and linked (file permalink at that commit), fingerprints match filesystem scans (no duplicates). Verified live: a secret deleted at HEAD is auto-resolved by an incremental scan and re-opened by a full scan with the first commit as its origin.
- [x] **T54. Shared Git cache (ADR 0001)** implemented (PVC, bare mirrors, `flock`, private clones from the mirror), Helm values/PVC template, dev PVC; verified live (mirrors for both repositories on the PVC, repositories `warm`). Docs no longer claim a non-existent feature.
- [x] **T55. Per-provider PR tokens** (`renovate-token-<provider>` with fallback); missing token fails the run with a message naming the key. Verified live (Gitea works; a GitHub repo without a token fails clearly).
- [x] **T56. Alerts are operable:** acknowledge / snooze 24h / reopen, stored server-side and audited, with a "show acknowledged" toggle; the bell counts only active alerts.
- [x] **T57. Webhook moved to Settings** with an HMAC-SHA256 signing secret (write-only) and a *Send test event* button; verified live with a receiver container (signature recomputed and matched).
- [x] **T58. Policy governance view:** ScanPolicy resources listed read-only with schedules, limits, status and repository counts.
- [x] **T59. Assignee, SLA and MTTR:** assignee per finding (audited), remediation due date from per-severity SLA days (editable in Settings), overdue flag, `findings/stats` (open, overdue, mean time to resolve), shown in the Findings header; resolved timestamps backfilled by the migration.
- [x] **T60. Repositories table:** health filter, sort (name / most findings / health), paging (15/page).
- [x] **T61. Finding focus from the repo page** (opens that finding expanded), saved views for findings filters (browser-local), j/k row navigation, "Re-scan to re-verify" for secrets, CSV export for runs and audit (findings already), global search across repositories, findings and runs, and a visibly disabled "Run Scan" with a reason for read-only roles.
- [x] **T62. Slash repository ids** (`owner/name`, what discovery creates) could not be deleted/unblocked/queried via `/repositories/{id}...`; clients now send `~` for `/` (found by the e2e cleanup).
- [x] **T63. AOT publish checked for real** in an SDK 10 container with clang: the project is **not NativeAOT-compatible** and cannot be made so cheaply (EF Core `MigrateAsync` requires dynamic code, reflection-based `System.Text.Json` and `AsQueryable` are used throughout, KubeOps is reflection-based) — and this predates this work. CI's `aot-probe` job (non-gating, `continue-on-error`) was additionally failing earlier on `restore` without a runtime identifier (NETSDK1047), so it never reached these errors. It is left as the maintainers' probe; the shipped artifact is the regular container (`Dockerfile`), which builds and runs in compose.

- [x] **T64. Authorization bypass fixed:** re-posting an existing repository (operator role) no longer resets `Blocked` to `Ready`; unblocking stays admin-only. Verified live (status survives, operator unblock = 403).
- [x] **T65. Incremental scans no longer resolve history findings:** a finding with an introducing commit is only resolved by a full scan (no more resolve/reopen flip-flop, no skewed MTTR). Unit-tested and verified live.
- [x] **T66. Block gate respects triage:** a repository is blocked only by *open* verified findings; an ignored / false-positive decision sticks.
- [x] **T67. 200-row ceiling removed:** lists load every page (cap 10 000 rows) and show a visible "Showing the newest N of M" notice if truncated.
- [x] **T68. Settings audit has values** (before/after per changed key; the signing secret only as "changed").
- [x] **T69. Demo repositories are opt-in** (`AEGIS_SEED_DEMO=true`, set in compose only) instead of shipping in the production migrations; the obsolete `cacheRetentionGB` seed is gone; a migration removes the old demo rows and their runs/findings.
- [x] **T70. Dashboard consistency:** every run figure honours the selected range (KPIs, donut, activity), the legend includes canceled runs, the severity list is ordered, the attention table has headers; Renovate runs show no meaningless "Incremental" mode; policy wording explains global switches vs ScanPolicy resources and that earlier PRs stay linked.

- [x] **T71. Blocked is durable on every write path** (round 5): a CRD reconcile (operator restart, CRD edit, discovery) no longer resets `Blocked` to `Ready`, and only an admin can delete a blocked repository (delete + re-add would clear the block and discard triage history). Verified live with a CRD-managed repository.
- [x] **T72. Secret findings are only auto-resolved by full scans** (a secret deleted from the latest files is still live in history); incremental scans keep resolving dependency findings. Replaces the narrower T65 rule; unit-tested and verified live.
- [x] **T73. Webhook URL is write-only for non-admins** (`(configured)` for viewers/operators; Slack/Teams-style URLs are credentials).
- [x] **T74. Alert acknowledgements reset** when the underlying signal changes (repository re-blocked or unblocked, finding reopened or triaged).
- [x] **T75. Runs whose Job disappeared are failed** after 2 minutes instead of holding a concurrency slot forever; early queue failures now set `FinishedAt`.
- [x] **T76. Git cache robustness:** the mirror's `origin` is re-pointed to the current clone URL on every run (a changed or recreated repository can no longer fetch the old one) and mirror directories carry an id hash (`a/b` and `a_b` no longer collide).
- [x] **T77. Custom scanner raw output is not stored** (a secret scanner's output would contain the secrets); see T83 for how it is interpreted instead.
- [x] **T78. Wording/UX:** the activity legend says it covers the last 12 runs; remediation text distinguishes incremental from full scans; "Re-scan to re-verify" now runs a full scan.

- [x] **T79. Vulnerability enrichment for Renovate findings:** the current version of each dependency is looked up in OSV.dev (no token; GHSA/CVE ids with links, cached 6 h, best-effort, switchable with the `vulnerabilityLookup` setting for air-gapped installs); a vulnerable current version raises the finding to **high**. Verified live (minimist 1.2.5 etc. from real OSV data).
- [x] **T80. Line anchors for Renovate manifest links:** the manifest is read from the forge at the scanned commit (Gitea/GitHub/GitLab raw URLs) and each dependency's line is stored, so "package.json ↗" opens at the dependency. Verified live against the real file in Gitea (the anchored line contains the dependency).
- [x] **T81. CI cleanup:** the `aot-probe` job was removed — NativeAOT cannot work for this project (EF Core migrations, reflection-based JSON, KubeOps; verified in an SDK 10 + clang container) and the job had been failing before it ever compiled. The shipped artifact is the regular container.

- [x] **T82. Round-6 review (verdict: SIGN OFF) — its six non-blockers fixed:** manifest fetches are restricted to the repository's own forge host (no redirects, no `..`/backslash paths, 1 MB bounded stream read) with a unit test; a failed vulnerability lookup is written to the run output as a warning instead of looking like "no advisories"; the `vulnerabilityLookup` (OSV) opt-out is a Settings checkbox with an explanation of what is sent; the "resolved" note no longer claims history knowledge; the Findings header labels its overdue/MTTR numbers as overall (not filtered).

- [x] **T83. Custom scanners are interpreted, not ignored:** each scanner has an output contract (`outputFormat` sarif | aegis-json | none, `category`, `successExitCodes`). A generic SARIF 2.1.0 parser and an Aegis JSON v1 parser (`GenericResultParser`) turn any such scanner's output into findings with rule, severity (`security-severity` / level; secrets are never below high), path:line, rule documentation link and a *masked* preview (raw snippets are never stored); stable fingerprints; auto-resolve by category.
- [x] **T84. Honest run status:** unparseable output fails the run with a clear reason (no retries, never "0 findings"); format `none` shows a "Not interpreted" badge instead of a count; exit codes declared in `successExitCodes` (e.g. gitleaks exits 1 on findings) count as a successful scan, read from the scan container's terminal state.
- [x] **T85. Generic UI:** sidebar entries, scanner filter, finding title/remediation/preview and the dashboard secrets/updates split come from scanner data and category, not hard-coded ids; scanner editor has format/category/exit-code fields and a **Preview parsing** tool (`POST /scanners/preview-parse`) to see the findings before running; the Add page only adds (editing lives in each scanner's Settings tab).
- [x] **T86. Verified live in k3s against Gitea** (`scripts/custom-scanners-e2e.sh`): Aegis JSON wrapper, real gitleaks (exit 1) and Trivy containers via SARIF, broken-output failure, `none` flag, preview-parse; Playwright `custom-scanners.spec.ts` (compose) with screenshots. Docs: `docs/operations/custom-scanners.md`.

### Not implemented
Nothing from the review/brief lists remains open. Deliberate scope notes: alerts are acknowledged per key (not per user); saved views are browser-local; the mirror cache has no automatic size eviction (the PVC size is its bound).

## Notes / out of scope
- Rancher Desktop's Kubernetes was disabled, so the bundled compose k3s was used (user confirmed). Remote Rancher contexts were not touched.
- Private repository credentials (clone auth) and non-GitHub discovery remain future work.
