# AegisScan Design Enhancements

UX review of the running app (`http://localhost:4173/`). Layout and information architecture are good — keep them as-is. This doc lists what's flat, broken, or inconsistent, ranked by impact, with concrete fixes an implementing agent can act on directly. Everything below is scoped to `web/src/main.tsx` and `web/src/styles.css`.

## P0 — Fix broken visualizations

### 1. "Execution activity" chart renders as two flat color blocks, not a chart
Dashboard, `main.tsx` line ~81 (`live-chart` bars), styled in `styles.css` under `.live-chart`. Each run is a flex bar (`flex:1`) whose height comes from a status-derived index formula (`Math.max(14, 100 - i*5)}%`), not from any real metric. With only 1-2 runs, `flex:1` stretches each bar to fill the whole panel width, producing a giant solid-color rectangle (green for `succeeded`) instead of a legible chart. This is the biggest "looks broken" moment in the app.

**Fix:**
- Cap bar width (e.g. `max-width: 28px`) and left-align bars instead of stretching to fill via `flex:1`, so sparse data doesn't turn into a wall of color.
- Bar height should encode something real — run duration, or count of runs bucketed by day — not an arbitrary index-based falloff.
- The panel legend says "Renovate" (violet) / "TruffleHog" (red), but bars are colored by **status** (`succeeded`=green, `failed`=red, `running`=blue), not scanner type. Legend and bar coloring must match: either recolor bars by scanner and add a status indicator (dot/border), or change the legend to reflect status.
- Add a minimum-data empty/sparse state (e.g. "Not enough runs yet for a trend" placeholder) when `runs.length < 5`, rather than rendering misleading giant blocks.

### 2. Donut chart CSS exists but is unused — severity/status panels are plain text lists
`styles.css` defines a full donut chart system (`.donut`, `.donut-layout`, `.donut-legend`, `spin-in` animation) but the "Run status breakdown" and "Findings by severity" panels (`main.tsx` ~line 81) render as plain `<div>` lists with no visual chart at all. These are the two panels most likely to "wow" — turn them into actual donut/ring charts using the SVG `stroke-dasharray` technique (no chart library needed, matches the existing hand-rolled sparkline/line-chart approach already in the codebase).

**Fix:** Build the ring from stacked `<circle>` `stroke-dasharray` segments (one per status/severity), animate the dash-offset in on mount (reuse the existing `spin-in` keyframe), keep the center label (`<strong>{count}</strong>`). This is a ~30-line component, consistent with how `Spark` (line ~46) is already built.

### 3. Sparklines in metric cards are minimal — make them earn their space
`Spark` component (`main.tsx` ~line 46) draws a polyline/polygon area from `metrics[].data`, but that data is a synthetic 0/1 array (`map(r => status ? 1 : 0)`), so the sparkline is always a flat step function, never an interesting shape. Feed it real time-bucketed values (counts per day/hour over the selected range) so the line actually moves.

## P1 — Motion & interaction

The only animations in the app are `rise`, `spin-in`, `toast-in`, `fade-in`, `log-pulse`, `running-pulse` (`styles.css` lines 4, 7, 12) applied narrowly to card mount and the toast. Everything else is instant — page swaps, panel content, number values, hover states beyond `.primary:hover`. This is the main source of "no wow factor."

- **Route/section transitions:** wrap the page body swap (whatever renders under `.content-wrap` per nav item) in a fade+slight-slide transition (150-200ms) instead of an instant DOM swap.
- **Animated counters:** metric card numbers (`.metric-copy strong`) and the severity summary number (`.severity-summary strong`) should count up from 0 on mount/update instead of snapping to the final value. A small `requestAnimationFrame` easing helper is enough — no library needed.
- **Hover depth on cards/panels:** `.metric-card` and `.panel` currently have no hover state at all. Add a subtle `transform: translateY(-2px)` + shadow lift on hover, consistent with the existing `.primary:hover` treatment, to make the dashboard feel interactive rather than static.
- **Skeleton loading states:** check how `loading-banner` (`styles.css` line ~11) is used today — it looks like a static text banner. Replace with skeleton placeholders (shimmering gray blocks matching each panel's real shape) for metric cards, tables, and charts while data loads, instead of a banner + empty panel.
- **Table row stagger:** `.table-row` / `.feature-row` appear all at once. A small staggered fade-in (each row delayed ~20ms after the previous, same pattern as `.metric-card`'s `--delay` variable) makes data-heavy pages feel less static.
- **Live pulse consistency:** `.log-live-dot.active` and `.pill.running` already pulse nicely (lines 7, 12) — extend this same treatment to any other "live" indicator (e.g. the sidebar `.live-dot`, which is currently static).

## P1 — Code/config surfaces have no syntax coloring

Several places take or show structured config as plain, unstyled text — flagged by the user as looking out of place against the rest of the themed UI:

- **Settings → Scanner catalog** (`main.tsx` ~line 124-126): "Command (one item per line)" and "Startup parameters (one item per line)" are plain `<textarea>` elements with no monospace font, no line numbers, no placeholder-token highlighting (`{{workspace}}`, `{{repository}}`, `{{branch}}`, `{{mode}}` are just plain text in the helper caption, not highlighted in the field itself).
  - **Fix:** switch these textareas to `font: 12px 'DM Mono', monospace` (already loaded and used elsewhere, e.g. `.version`, `.telemetry`) and add a lightweight regex-based highlight of `{{...}}` tokens (wrap matches in a `<mark>`/`<span>` with the accent violet color) via a `contenteditable` overlay or a simple textarea-with-highlighted-backdrop pattern. Don't reach for a full code-editor dependency (CodeMirror/Monaco) for single-line command args — that's overkill for this field.
- **Full scan schedule (cron field)**, Settings page: plain text input for a cron expression (`0 3 * * 0`). Give it the same monospace treatment and add inline human-readable translation below it (e.g. "Runs every Sunday at 3:00 AM") — cron is notoriously unreadable and this is a cheap, high-value addition.
- **Run log output** (`.run-log-output`, `styles.css` line 7) is already dark, monospace, and well-styled — good baseline to match the above against. If run logs ever include YAML/JSON blocks, add basic color tokens (keys, strings, comments) rather than leaving it monochrome `#d7e5f5`.

## P1 — Dark theme inconsistencies ("white blocks")

- **Native `<select>` dropdowns:** `.heading-actions select`, `.panel-head select`, `.modal select` (date range, scan mode, severity threshold) are styled on the closed control, but the open dropdown list uses the OS-native popup, which renders as a plain white list on most platforms — a jarring flash of light theme inside an otherwise fully dark UI. Since a real custom `<select>` is more than a one-line fix, the lazy-but-correct option is a small custom listbox component (button + absolutely-positioned `<ul role="listbox">` styled with the existing `.panel`/`.modal` dark palette) reused across all four dropdown sites — one component, not four one-offs.
- **Policies page is almost entirely empty space.** Only two toggles and one select render inside a full-height panel (`main.tsx`, Policies view), leaving a large unstyled dark void below the fold. Either add more policy content (per-repo overrides, notification routing, approval chain preview) or constrain the panel's height/`min-height` so it doesn't look like a broken/half-loaded page.
- **Low-contrast/"disabled-looking" labels:** on the Policies page, field labels ("Minimum finding severity", "Block verified findings", "Requires scan approval") render in the same muted gray as helper captions, making active controls look disabled. Bump label color to the primary text color (`#e8edf5` / `--text`) and reserve the muted tone (`#8a9ab0` / `--muted`) strictly for helper/caption text underneath.

## P2 — Color & polish

- Chart/legend color mapping is inconsistent across the app: severity uses `critical`/`high`/`medium`/`low` classes with one palette (`styles.css` line 2-ish `--red`, `--amber`, etc.), while run status uses a different green/blue/red set, and scanner identity (Renovate/TruffleHog) uses violet/red. Document one palette-to-meaning mapping (status colors vs. severity colors vs. scanner-identity colors) and apply it consistently so a color always means the same thing across panels.
- Add a subtle glow/gradient accent to the primary metric numbers (`.metric-copy strong`) for the top-line stats — e.g. a soft `text-shadow` in the tone color — to give the dashboard a focal point instead of uniform white numerals.
- `.system-card` / `.system-overview` "live" telemetry is static text (`--dot` aside). Consider a tiny animated tick (last-updated timestamp counting up in seconds) to reinforce that the platform is live, reusing the `log-pulse` keyframe already defined.

## Explicitly out of scope

- No new chart/animation library (Chart.js, Framer Motion, etc.) — the app already hand-rolls SVG sparklines and CSS keyframes; extend that pattern rather than adding a dependency for a handful of shapes.
- No full code-editor component (Monaco/CodeMirror) for the scanner command/params fields — a monospace font + token highlight covers the actual need.
- Don't touch layout/IA (sidebar, nav structure, page grid) — only visuals, motion, and the broken chart.
