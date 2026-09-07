# Action plan: remaining findings from the enterprise-readiness pass

Source: the three items left open at the end of `tasks.md` (2026-09-06) — the frontend has no
real login flow, and P3 items #19/#20 were deliberately left unimplemented because they need
product decisions the codebase doesn't answer yet. This document is a plan, not an
implementation — nothing here has been built. Each section lays out the problem, the decision
points, a concrete step-by-step approach, and how it'll be verified once built, following the
same "implemented + tested against real infrastructure" bar as `tasks.md`.

Recommended sequencing: **do #1 first.** It's the only one of the three that's actually blocking
— the backend is correctly secured and the shipped UI currently cannot reach it at all. #2 and #3
are real gaps but not blockers.

---

## 1. Frontend OIDC login flow (Authorization Code + PKCE)

### Problem

The backend enforces real JWT bearer auth (`tasks.md` #1). The frontend never adopted it — it
still derives "role" from a `?role=admin` URL query parameter and sends an `X-Aegis-Role` header
the backend has never read. Every API call from the shipped UI now gets `401`. This is the one
finding that makes the product not work end-to-end today, so it's the priority.

### Decision points (recommendations given, flag if you want something different)

- **Library vs. hand-rolled PKCE.** Recommend **`oidc-client-ts`** (actively maintained, the
  de-facto standard for SPA OIDC — used under the hood by most framework-specific OIDC packages).
  Hand-rolling PKCE/state/nonce validation is exactly the kind of security-sensitive code with
  subtle failure modes (state-fixation, code-verifier storage, silent-renew races) that's worth
  not reinventing.
- **Token storage.** Recommend `sessionStorage` (via the library's built-in `WebStorageStateStore`)
  over `localStorage` — scoped to the tab, cleared on close, still workable for silent renew.
  Pure in-memory-only storage would be more XSS-resistant but loses the session on every page
  refresh, which is a worse trade for this app's usage pattern.
- **Silent renew mechanism.** Keycloak supports the standard hidden-iframe silent-renew flow;
  recommend that over relying solely on refresh-token rotation, since it needs no extra backend
  work. Needs Keycloak's client redirect URIs to include a silent-renew callback path and CORS
  configured for the web origin (already same-origin in compose via the Vite proxy, but a real
  deployment's web origin needs adding to Keycloak's client config).
- **Role source of truth.** Once the ID token carries real `groups`, `permissions.ts` should read
  the role from the authenticated user object, not the URL. `?role=` becomes dead and should be
  removed rather than left as a confusing second, now-ignored code path.

### Plan

1. **Add the dependency**: `oidc-client-ts` to `web/package.json`.
2. **Configure the client** (`web/src/auth.ts`, new file): `UserManager` pointed at Keycloak's
   issuer (`Authentication:Authority` — same value the backend already uses, so both sides agree),
   `client_id: aegisscan-web`, `response_type: code`, `scope: openid`, redirect URI, silent-renew
   URI, `WebStorageStateStore` backed by `sessionStorage`.
3. **Gate the app shell on auth state**: on load, if no valid user, redirect to Keycloak login
   (`userManager.signinRedirect()`); on the callback route, complete the code exchange
   (`signinRedirectCallback()`) and restore the originally-requested URL.
4. **Attach the token**: update `web/src/dataClient.ts`'s `request()` to read the current access
   token from the auth client and send `Authorization: Bearer <token>` — replacing the
   `X-Aegis-Role` header entirely.
5. **Derive role from claims**: update `web/src/permissions.ts` (`canRunScans`/`canConfigure`) to
   accept the real `groups` claim from the ID token instead of a role string parsed from the URL.
   Update `main.tsx`'s call sites accordingly.
6. **Add logout**: a real sign-out button calling `userManager.signoutRedirect()`, replacing
   whatever the current profile-button behavior implies about identity.
7. **Handle 401s from an expired/invalid token**: a fetch-wrapper check in `dataClient.ts` that
   triggers `signinRedirect()` on a 401 instead of surfacing "Live data could not be loaded"
   forever.
8. **Update Docker Compose / Helm wiring**: the built web container needs to know the Keycloak
   authority and client ID at runtime (a small `env.js` injected at container start, matching how
   `VITE_API_BASE_URL` is already handled, since Vite bakes env vars in at *build* time and the
   image is built once but deployed with different authorities per environment).
9. **Update tests**:
   - Unit tests (`permissions.test.ts`) — change role-derivation inputs from a role string to a
     mock claims object.
   - E2E tests (Playwright) — every spec currently does `page.goto('/?role=admin')`; these need a
     real login step against the compose Keycloak instance first (fill the Keycloak-hosted login
     form, submit, land back on the app). This is the biggest test-suite change in this plan.

### Verification plan

- Log in as each of the three seeded Keycloak users (admin/operator/viewer) through the real
  browser flow; confirm the UI correctly shows/hides operator and admin actions based on the
  *token's* claims, not a URL parameter.
- Confirm API calls succeed with a real `Authorization: Bearer` header (the `401`s found during
  the #18 verification pass should disappear).
- Let a token expire (or force it) and confirm silent renew keeps the session alive without a
  full page reload; confirm an actually-invalid/revoked token triggers a clean re-login instead of
  a stuck error banner.
- Full Playwright suite green against the real compose stack (Postgres + Keycloak + k3s +
  operator + web), not just against fixture-mode data.

### Rough effort

The auth plumbing itself (steps 1–7) is a moderate, well-scoped chunk of frontend work. Step 9
(rewriting every E2E spec's login step) is likely the larger share of the total effort, just by
volume of specs touched.

---

## 2. Enforce scan-policy gating settings (`tasks.md` #19)

### Problem

`PolicyView` already has UI for three settings — "block verified findings," "minimum finding
severity," "require scan approval" — but they're stored and displayed only; nothing in the scan
pipeline reads them. Two of the three can't be enforced with the current data model as-is, which
is why this was left open rather than half-implemented.

### Decision points (need a call before building this — these change the data model)

- **Severity doesn't exist today.** `FindingEntity` has no severity field, and TruffleHog's
  output has no severity concept — only `DetectorName` (e.g. `AWS`, `PrivateKey`, `URI`) and
  `Verified`. "Minimum severity" requires a severity taxonomy from somewhere. Two options:
  - **(a) Static detector→severity mapping** maintained in this codebase (e.g. `AWS`/`PrivateKey`
    → `critical`, generic `URI` → `low`), computed at parse time in `TruffleHogResultParser`.
    Simple, but it's an editorial judgment call this codebase would be making on your behalf,
    and it only covers TruffleHog — any other scanner added later needs its own mapping.
  - **(b) Treat `Verified` as the only real signal** (which is what TruffleHog itself actually
    guarantees) and drop "minimum severity" as a policy lever until a scanner that provides real
    severity is integrated. Recommend **(b)** — inventing a severity scale not backed by the
    underlying tool is the kind of thing that looks like a real feature but isn't, and it's a
    reasonable scope-cut given only one scanner is wired up for real execution today.
- **What does "block" actually do?** There's no CI/CD gate or deployment concept in this system —
  it's a scanner + API, not a pipeline gate. Two compatible options, recommend building both since
  they're cheap once the first exists:
  - Reflect it in `Repository.Status` (e.g. `"Blocked"`) so it's visible in the existing
    `GET /api/v1/repositories` response and UI without any new surface.
  - Add a dedicated `GET /api/v1/repositories/{id}/gate` → `{ blocked: bool, reason }` endpoint,
    specifically meant for an external CI job to call as its actual gate check.
- **Approval workflow scope.** "Require scan approval" implies a new execution state machine:
  `queued` (a repository/policy that doesn't require approval works as today) vs.
  `pending_approval` (created by `POST /runs` when the effective policy requires it, invisible to
  `ScanExecutionWorker` until approved) → an explicit approve action moves it to `queued`. Confirm
  this is the intended shape before building it — it changes the execution lifecycle other code
  already depends on (retry/backoff logic, `SchedulingWorker`'s scheduled scans, metrics).

### Plan (assuming the recommendations above)

1. Add `RepositoryEntity.Status = "Blocked"` as a new valid status value; `ScanExecutionWorker`
   already treats non-`"Ready"` statuses as skippable for scheduled scans (`SchedulingWorker`'s
   `PolicyRef` query already excludes `"Disabled"`/`"Error"` — extend that exclusion list, or
   invert it to an explicit allow-list of `"Ready"`).
2. On scan completion in `ScanExecutionWorker.ProcessRunningAsync`: if the effective policy's
   `blockVerifiedFindings` is true and any *newly persisted* finding this run has `Verified == true`,
   set `Repository.Status = "Blocked"`. Blocking should be explicit and reversible — add an
   `POST /api/v1/repositories/{id}/unblock` (Admin-only) rather than requiring a direct DB edit.
3. Add `GET /api/v1/repositories/{id}/gate` returning `{ blocked, reason }` for CI integration.
4. Add `ExecutionEntity.Status = "pending_approval"` and `POST /api/v1/runs/{id}/approve`
   (Operator/Admin). `POST /api/v1/runs` checks the effective policy's `requireScanApproval` and
   creates the execution in `pending_approval` instead of `queued` when set.
   `ScanExecutionWorker.ProcessQueuedAsync`'s query already filters on `Status == "queued"`, so
   `pending_approval` rows are naturally invisible to it until approved — no change needed there.
5. Wire `PolicyView`'s existing settings (already collected via `PUT /api/v1/settings`) as the
   source for the "effective policy" read in steps 2 and 4 — this part already works today and
   just needs the backend to actually read the values it already stores.
6. Drop the "minimum finding severity" control from `PolicyView` (per the (b) decision above), or
   relabel it clearly as "not yet enforced" if you'd rather keep the UI placeholder visible.

### Verification plan

- Set `blockVerifiedFindings: true`, run a scan against a repository known to contain a verified
  secret (the same `trufflesecurity/test_keys` fixture used throughout `tasks.md`), confirm the
  repository flips to `Blocked` and the gate endpoint reports it; confirm `unblock` clears it.
- Set `requireScanApproval: true`, queue a run, confirm it lands in `pending_approval` and is
  never picked up by the worker; approve it, confirm it then runs to completion normally.
- Confirm a repository/policy *without* these settings enabled behaves exactly as it does today
  (no regression to the existing, already-verified scan pipeline).

### Rough effort

Small-to-moderate once the decisions above are confirmed — most of the plumbing (settings read,
worker hooks, new small endpoints) mirrors patterns already built for #4/#6/#16 in `tasks.md`.

---

## 3. CRD upgrade automation & accessibility polish (`tasks.md` #20)

These are two unrelated things that were bundled under one gap-analysis line item; splitting them.

### 3a. CRD upgrade automation

**Problem**: `docs/operations/crd-upgrades.md` documents a fully manual procedure (apply CRDs
before `helm upgrade`, inspect existing resources, back up YAML). "Automation" wasn't scoped
further than that in the original gap list.

**Decision point — pick one, they're not mutually exclusive:**
- **(a) A CI check**: on every PR touching `deploy/crds/*.yaml`, run a schema-diff against the
  previous version and fail the build if a field was removed or a type narrowed without a major
  version bump in the CRD's `apiVersion`. Catches breaking changes before release, doesn't touch
  what happens at install time.
- **(b) A release-time compatibility probe**: in the `release` CI job (`.github/workflows/ci.yml`),
  after building the chart, run `kubectl apply --dry-run=server` for each CRD against a throwaway
  k3s cluster (the same pattern the Compose smoke test already uses) and fail the release if the
  API server rejects it.
- Recommend **(a)**, since it gives feedback at PR time rather than at release time, and doesn't
  need a live cluster in CI.

**Plan**: a small script (`scripts/check-crd-compat.sh` or a CI-native step) that diffs
`deploy/crds/*.yaml` against the version at the target branch's most recent tag, flags removed
fields/enum values, and wires it into the `verify` job in `.github/workflows/ci.yml`.

**Verification**: introduce a deliberately breaking CRD change in a branch, confirm the new CI
check fails; revert it, confirm it passes.

### 3b. Accessibility polish

**Problem**: the frontend already has some accessibility work in place (focus trap on modals, a
skip-to-content link, several `aria-label`s) but "polish" wasn't scoped against a concrete
baseline — there's no current audit to work from.

**Plan**:
1. Add an automated baseline: `axe-core` run via Playwright (`@axe-core/playwright`) as a new
   assertion in the existing E2E specs — one `AxeBuilder` check per major view (Dashboard,
   Repositories, Scans, Findings, Settings, Policies).
2. Triage what it reports (contrast, missing labels, focus order, landmark structure) into
   concrete fixes — can't be sized meaningfully until step 1 produces a real list, since the
   codebase has no accessibility audit to plan against today.
3. Fix findings; re-run the automated check until clean; spot-check keyboard-only navigation
   through the modals (scan, repository, discovery) since those are the highest-risk interactive
   surfaces already in the codebase.

**Verification**: `axe-core` assertions passing in CI on every relevant view, plus a manual
keyboard-only pass through the primary flows (queue a scan, add a repository, change settings).

**Rough effort**: step 1 is small; steps 2–3 are unsized until the audit exists — expect this to
be the least predictable estimate of anything in this plan.

---

## Summary of what needs your input before any of this is built

1. **#1 (frontend login)**: confirm `oidc-client-ts` + `sessionStorage` + silent-renew-via-iframe
   is an acceptable approach, or say if you want something else.
2. **#19 (policy gating)**: confirm dropping "minimum severity" (option b) rather than inventing a
   detector→severity mapping (option a); confirm the `pending_approval` execution-state shape is
   what you want "require scan approval" to mean.
3. **#20a (CRD automation)**: confirm a PR-time CI diff check (option a) over a release-time
   cluster probe (option b), or both.
4. **#20b (accessibility)**: no decision needed to start — the first step (adding `axe-core`) is
   safe to do unconditionally and will produce the list everything else depends on.
