# AegisScan application test plan

Final status: complete. Each item below was exercised through the real browser flow, existing automated tests, or the listed verification command.

## Environment and global shell

- [x] Dev server starts and serves `/`.
- [x] TypeScript check, unit tests, production build, and Playwright E2E suite pass.
- [x] Dashboard opens by default with active navigation state.
- [x] Primary navigation works for Dashboard, Repositories, Scans, Findings, Alerts, Policies, and Settings.
- [x] Scans submenu opens Renovate and TruffleHog filtered views.
- [x] Administration navigation works for Users, Roles, Audit Logs, and System.
- [x] URL view state and browser back/forward navigation work.
- [x] Profile control opens Profile and profile values persist through reload.
- [x] Search filters dashboard repositories and `Ctrl/Cmd+K` focuses search.
- [x] Notifications opens Alerts; Help shows a status notice.
- [x] Dark/light mode toggles and persists through reload.
- [x] Date range options update dashboard copy.
- [x] Desktop and mobile layout smoke coverage exists in the browser test workflow.

## Dashboard

- [x] Repository, in-progress, successful, active-finding, and failed-run metrics render.
- [x] Execution activity, run breakdown, severity, recent runs, attention, activity, and quick-action panels render.
- [x] Dashboard links to runs, findings, audit, repositories, scans, and policies work.
- [x] Empty-state components render when collections are empty.

## Repositories

- [x] Repository rows render provider, branch, cache, finding count, and status.
- [x] Repository filter is case-insensitive and updates visible rows.
- [x] Admin can open the add form and create a repository.
- [x] Required fields prevent empty repository submission.
- [x] Add modal closes with Cancel, close button, backdrop, and Escape.
- [x] Remove confirmation can be dismissed without deleting the repository.
- [x] Viewer cannot see repository configuration actions.

## Scans and execution details

- [x] Run Scan opens with repository, scanner, and mode controls.
- [x] Incremental and full scan submissions work; full scan requires accessible alert-dialog confirmation.
- [x] Full-scan confirmation can be canceled or closed.
- [x] Escape closes scan dialogs.
- [x] Scans, Renovate, and TruffleHog filters render expected execution history/empty states.
- [x] Authorized queued runs can be canceled and update to canceled status.
- [x] Logs open and close; fixture mode shows representative TruffleHog/Renovate output without making 404 API requests.
- [x] Viewer cannot launch scans; operator can launch confirmed scans.

## Findings, alerts, policies, settings, and administration

- [x] Findings screen renders sanitized finding fields and supports filtering.
- [x] Alerts renders failed runs/open findings and its review navigation.
- [x] Policies controls render and authorized policy saves show confirmation.
- [x] Settings controls render and authorized settings saves show confirmation.
- [x] Users supports authorized local add/remove persistence.
- [x] Roles renders identity-provider guidance and its settings CTA.
- [x] Audit Logs renders its table/empty state.
- [x] System renders API, repository, execution, and runtime status.
- [x] Profile fields save to local storage and reload with saved values.

## Accessibility and resilience

- [x] Scan/repository dialogs expose dialog semantics, labels, close actions, and keyboard dismissal.
- [x] Main buttons, navigation, form controls, and search have accessible names.
- [x] Loading/error/status/toast patterns are present and do not crash the UI.
- [x] Browser console has no errors during the final fixture-mode flow.

## Evidence

- `npm run typecheck` — passed.
- `npm test` — 4 files, 8 tests passed.
- `npm run build` — passed.
- `npm run test:e2e` — 6 tests passed.
- Manual Playwright CLI browser pass — all 13 destinations, admin/operator/viewer permissions, repository CRUD, scan confirmation/cancel/logs, filters, settings, policies, users, roles, audit, system, profile, theme, search, date range, and history.
