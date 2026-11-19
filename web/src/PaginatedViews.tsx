import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

export type Repository = { id: string; name: string; provider: string; cloneUrl: string; defaultBranch?: string; webUrl?: string; latestCommitSHA?: string; health?: string }
type RunLink = { kind: string; title: string; url: string }
type Run = {
  id: string; scanner: string; repositoryId: string; mode?: string; status: string; attempt?: number; findingCount?: number; outputInterpreted?: boolean
  createdAt?: string; startedAt?: string; finishedAt?: string; sanitizedLog?: string; failureReason?: string; commitSHA?: string; commitUrl?: string; links?: RunLink[]
}
type Finding = {
  fingerprint: string; repositoryId: string; detector: string; verified: boolean; safePath: string; commitSHA?: string
  firstSeen?: string; lastSeen?: string; state: string; scanner?: string; severity?: string; detail?: string; executionId?: string
  preview?: string; category?: string; helpUrl?: string; fileUrl?: string; commitUrl?: string; pullRequestUrl?: string; pullRequestNumber?: number; changelogUrl?: string
  advisories?: { id: string; summary: string; url: string }[]; sourceLine?: number; introducedCommit?: string; introducedCommitUrl?: string; assignee?: string; dueAt?: string; overdue?: boolean
}
type FindingStatsView = { open: number; overdue: number; mttrDays: number | null; resolvedLast90Days: number }
type HistoryItem = { timestamp: string; actor: string; from: string; to: string; reason: string }
const REASON_STATES = ['ignored', 'false_positive']
const VIEWS_KEY = 'aegis-findings-views'
const readViews = (): Record<string, any> => { try { return JSON.parse(localStorage.getItem(VIEWS_KEY) || '{}') } catch { return {} } }
type Pretty = (value: any) => string
type RelativeTime = (value?: string) => string

/** Pre-applied filters for the findings view, set by drill-down links elsewhere ("12 findings" on a run, repository counts...). */
export type FindingsPreset = { repositoryId?: string; executionId?: string; fingerprint?: string }

/** Lets deep components link to other views without prop-drilling: in-app navigation, findings drill-down, opening a run's output. */
export const NavigationContext = createContext<{ navigate: (view: string) => void; openFindings: (preset: FindingsPreset) => void; openRun: (executionId: string) => void }>({
  navigate: () => {}, openFindings: () => {}, openRun: () => {}
})

const short = (sha?: string) => (sha || '').slice(0, 7)
export const forgeName = (provider?: string) => ({ gitlab: 'GitLab', gitea: 'Gitea', bitbucket: 'Bitbucket' }[(provider || '').toLowerCase()] || 'GitHub')

export function Pager({ page, pages, onChange }: { page: number; pages: number; onChange: (page: number) => void }) {
  if (pages <= 1) return null
  return <nav className="pagination" aria-label="Pagination">
    <button className="secondary" disabled={page === 1} onClick={() => onChange(page - 1)}>Previous</button>
    <span>Page {page} of {pages}</span>
    <button className="secondary" disabled={page === pages} onClick={() => onChange(page + 1)}>Next</button>
  </nav>
}

/** Link that leaves the app (forge pages). Never toggles a parent row. */
export function ExternalLink({ href, children, label }: { href?: string; children: ReactNode; label?: string; key?: string }) {
  if (!href) return <>{children}</>
  return <a className="ext-link" href={href} target="_blank" rel="noreferrer noopener" aria-label={label} onClick={e => e.stopPropagation()}>{children}<span aria-hidden="true"> ↗</span></a>
}

/** Browser URL of the repository: the server-computed one (honours forge URL overrides) or a best-effort fallback from the clone URL. */
export function repoHref(repo?: Repository) {
  if (repo?.webUrl) return repo.webUrl
  if (!repo?.cloneUrl) return ''
  return repo.cloneUrl.replace(/\.git$/, '').replace(/^git@github\.com:/, 'https://github.com/').replace(/^git@gitlab\.com:/, 'https://gitlab.com/')
}
export function ProviderIcon({ provider }: { provider: string }) {
  const key = provider.toLowerCase()
  const label = key === 'gitlab' ? 'GL' : key === 'bitbucket' ? 'BB' : key === 'gitea' ? 'GT' : 'GH'
  return <span className={`provider-icon provider-${key}`} aria-hidden="true">{label}</span>
}

/** Repository name opens the in-app report; the arrow opens it on the forge. */
export function RepositoryLink({ repo, id }: { repo?: Repository; id: string }) {
  const { navigate } = useContext(NavigationContext)
  const href = repoHref(repo)
  const name = repo?.name || id
  return <span className="repo-link">
    <ProviderIcon provider={repo?.provider || 'github'}/>
    {repo ? <button type="button" className="repo-name" aria-label={`Open ${name} report`} onClick={e => { e.stopPropagation(); navigate(repo.id) }}>{name}</button> : <span>{name}</span>}
    {href && <a className="ext-icon" href={href} target="_blank" rel="noreferrer noopener" aria-label={`Open ${name} in ${forgeName(repo?.provider)}`} title={`Open in ${forgeName(repo?.provider)}`} onClick={e => e.stopPropagation()}>↗</a>}
  </span>
}

const builtInScanner = (f: Finding) => f.scanner === 'trufflehog' || f.scanner === 'renovate'
const CATEGORY_FIX: Record<string, string> = {
  secrets: 'Rotate the credential, then remove it from the code and git history; mark it a false positive if it is not a real secret.',
  vulnerabilities: 'Upgrade or replace the affected component to a fixed version (see the rule documentation for the fixed release).',
  dependencies: 'Review the dependency update and apply it.',
  code: 'Fix the flagged code (see the rule documentation for the recommended pattern), or mark it a false positive.',
  iac: 'Correct the infrastructure configuration as described in the rule documentation.'
}
const remediation = (f: Finding) =>
  !builtInScanner(f) ? (CATEGORY_FIX[f.category || ''] || 'Review the finding and fix or triage it.') + (f.helpUrl ? ' Rule documentation is linked above.' : '')
  : f.detector === 'RenovateDependency' ? 'Review the update and merge the pull request (or let Renovate open one from Policies). Major updates can contain breaking changes.'
  : f.detector === 'RenovateConfig' ? 'Fix the Renovate configuration in the repository so dependency updates can be computed.'
  : f.verified ? 'This credential is live. Rotate it now, then purge it from git history. An incremental rescan only inspects the current files (only a full scan checks git history), so deleting the file alone does not make the credential safe.'
  : 'Confirm whether this is a real credential. If so rotate it and purge it from history (an incremental rescan only checks the current files); otherwise mark it as a false positive.'
const triageStates = [['resolved', 'Mark resolved'], ['ignored', 'Ignore'], ['false_positive', 'False positive']]
const findingTitle = (f: Finding) => !builtInScanner(f) ? f.detector : f.detector === 'RenovateDependency' ? f.detail : f.detector === 'RenovateConfig' ? 'Renovate configuration problem' : `${f.detector} secret`
const lineOf = (f: Finding) => /^line (\d+)$/.exec(f.detail || '')?.[1]

/** The exact issue: what was found, where, and a safe preview of the match. */
function IssuePreview({ finding, pretty }: { finding: Finding; pretty: Pretty }) {
  const dependency = finding.detector === 'RenovateDependency'
  const line = lineOf(finding) ?? (finding.sourceLine ? String(finding.sourceLine) : undefined)
  return <div className="finding-preview">
    <div className="preview-head">
      <span className={`sev ${finding.severity}`}>{pretty(finding.severity)}</span>
      <strong>{findingTitle(finding)}</strong>
      {finding.verified && <span className="pill failed">Verified live</span>}
      {finding.helpUrl && <ExternalLink href={finding.helpUrl} label={`Open documentation for ${finding.detector}`}>Rule documentation ↗</ExternalLink>}
    </div>
    {dependency
      ? <div className="preview-code" role="group" aria-label="Update preview">
          <div className="preview-file"><ExternalLink href={finding.fileUrl} label={`Open ${finding.safePath} on the forge`}>{finding.safePath}</ExternalLink></div>
          <code>{finding.detail}</code>
          {(finding.advisories || []).length > 0 && <div className="advisories" role="group" aria-label="Known vulnerabilities"><b>Known vulnerabilities in the current version</b><ul>{finding.advisories!.map(a => <li key={a.id}><ExternalLink href={a.url} label={`Open advisory ${a.id}`}>{a.id}</ExternalLink>{a.summary && <> — {a.summary}</>}</li>)}</ul></div>}
          {!finding.pullRequestUrl && <small>No pull request yet — enable pull request mode in Policies.</small>}
        </div>
      : <div className="preview-code" role="group" aria-label="Match preview">
          <div className="preview-file"><ExternalLink href={finding.fileUrl} label={`Open ${finding.safePath} line ${line} on the forge`}>{finding.safePath || 'Path withheld'}{line ? `:${line}` : ''}</ExternalLink></div>
          {!builtInScanner(finding) && finding.detail && <p className="finding-message">{finding.detail}</p>}
          <code>{finding.preview || (builtInScanner(finding) ? 'The detector did not provide a preview.' : 'No code snippet is stored.')}</code>
          <small>{builtInScanner(finding) ? 'The secret itself is never stored; this is a masked match.' : 'Code snippets are never stored in full; the preview is masked.'}</small>
        </div>}
  </div>
}

/** Ignore / false-positive decisions need a reason (audit evidence); resolve and reopen apply immediately. */
function TriageButtons({ current, onApply }: { current: string; onApply: (state: string, reason?: string) => void }) {
  const [pending, setPending] = useState('')
  const [reason, setReason] = useState('')
  const choose = (state: string) => REASON_STATES.includes(state) ? setPending(state) : onApply(state)
  if (pending) return <span className="triage-reason"><input aria-label="Reason" autoFocus placeholder={`Why is this ${pending.replace('_', ' ')}? (required)`} value={reason} onChange={e => setReason(e.target.value)}/><button className="primary" disabled={!reason.trim()} onClick={() => { onApply(pending, reason.trim()); setPending(''); setReason('') }}>Confirm</button><button className="text-button" onClick={() => setPending('')}>Cancel</button></span>
  return <>{current === 'open' ? triageStates.map(([state, label]) => <button key={state} className="secondary" onClick={() => choose(state)}>{label}</button>) : <button className="secondary" onClick={() => choose('open')}>Reopen</button>}</>
}

function AssigneeEditor({ value, onSave }: { value: string; onSave: (value: string) => void }) {
  const [draft, setDraft] = useState(value)
  useEffect(() => setDraft(value), [value])
  return <span className="assignee-editor"><input aria-label="Assignee" placeholder="Unassigned" value={draft} onChange={e => setDraft(e.target.value)}/>{draft !== value && <button className="text-button" onClick={() => onSave(draft)}>Save</button>}</span>
}

function FindingDetails({ finding, repo, pretty, relativeTime, canTriage, onTriage, onAssign, onRescan, loadHistory }: { finding: Finding; repo?: Repository; pretty: Pretty; relativeTime: RelativeTime; canTriage?: boolean; onTriage?: (finding: Finding, state: string, reason?: string) => void; onAssign?: (finding: Finding, assignee: string) => void; onRescan?: (finding: Finding) => void; loadHistory?: (fingerprint: string) => Promise<HistoryItem[]> }) {
  const { openRun } = useContext(NavigationContext)
  const [copied, setCopied] = useState(false)
  const [history, setHistory] = useState<HistoryItem[]>([])
  const [allHistory, setAllHistory] = useState(false)
  useEffect(() => { let cancelled = false; loadHistory?.(finding.fingerprint).then(items => { if (!cancelled) setHistory(items) }).catch(() => {}); return () => { cancelled = true } }, [finding.fingerprint, finding.state])
  const forge = forgeName(repo?.provider)
  const copy = () => { navigator.clipboard?.writeText(finding.fingerprint).then(() => { setCopied(true); window.setTimeout(() => setCopied(false), 1500) }).catch(() => {}) }
  return <div className="finding-details">
    <IssuePreview finding={finding} pretty={pretty}/>
    <div className="finding-links">
      {finding.fileUrl && <ExternalLink href={finding.fileUrl}>Open file{finding.introducedCommit ? ' at the introducing commit' : ''} in {forge}</ExternalLink>}
      {finding.introducedCommitUrl && <ExternalLink href={finding.introducedCommitUrl}>Introduced in {short(finding.introducedCommit)}</ExternalLink>}
      {finding.commitUrl && <ExternalLink href={finding.commitUrl}>Commit {short(finding.commitSHA)}</ExternalLink>}
      {finding.pullRequestUrl && <ExternalLink href={finding.pullRequestUrl}>Pull request #{finding.pullRequestNumber}</ExternalLink>}
      {finding.changelogUrl && <ExternalLink href={finding.changelogUrl}>Changelog / releases</ExternalLink>}
      {repo && <ExternalLink href={repoHref(repo)}>Repository on {forge}</ExternalLink>}
      {finding.executionId && <button className="text-button" onClick={() => openRun(finding.executionId!)}>View the run that found it →</button>}
      {canTriage && onRescan && finding.scanner === 'trufflehog' && <button className="text-button" onClick={() => onRescan(finding)}>Full re-scan to re-verify</button>}
    </div>
    <div className="finding-detail-grid">
      <div><small>Repository</small><RepositoryLink repo={repo} id={finding.repositoryId}/></div>
      <div><small>Scanner</small><span>{pretty(finding.scanner)}</span></div>
      <div><small>Remediation due</small><span className={finding.overdue ? 'critical' : ''}>{finding.dueAt ? new Date(finding.dueAt).toLocaleDateString() : '—'}{finding.overdue ? ' · overdue' : ''}</span></div>
      <div><small>Assignee</small>{canTriage && onAssign ? <AssigneeEditor value={finding.assignee || ''} onSave={value => onAssign(finding, value)}/> : <span>{finding.assignee || 'Unassigned'}</span>}</div>
      <div><small>Last seen at commit</small>{finding.commitUrl ? <ExternalLink href={finding.commitUrl}><code>{short(finding.commitSHA)}</code></ExternalLink> : <code>{short(finding.commitSHA) || 'Not available'}</code>}</div>
      <div><small>First seen</small><span>{finding.firstSeen ? new Date(finding.firstSeen).toLocaleString() : '—'}</span></div>
      <div><small>Last seen</small><span>{finding.lastSeen ? `${new Date(finding.lastSeen).toLocaleString()} (${relativeTime(finding.lastSeen)})` : '—'}</span></div>
      <div><small>Finding fingerprint</small><span className="fingerprint"><code>{finding.fingerprint}</code><button className="text-button" onClick={copy}>{copied ? 'Copied' : 'Copy'}</button></span></div>
    </div>
    <div className="finding-remediation">
      <strong>How to fix</strong><span>{remediation(finding)}{finding.state === 'resolved' && finding.scanner === 'trufflehog' && <em> Resolved means handled, not safe: make sure the credential was rotated.</em>}</span>
      {canTriage && onTriage ? <span className="triage-actions"><TriageButtons current={finding.state} onApply={(state, reason) => onTriage(finding, state, reason)}/></span> : <small className="readonly-note">Read-only access: triaging findings requires the operator role.</small>}
    </div>
    {history.length > 0 && <div className="finding-history"><strong>History</strong> <small>({history.length} change{history.length === 1 ? '' : 's'})</small><ul>{(allHistory ? history : history.slice(0, 5)).map((h, i) => <li key={i}><b>{pretty(h.to)}</b> by {h.actor} · {relativeTime(h.timestamp)}{h.from ? ` (was ${pretty(h.from)})` : ''}{h.reason && <> — <em>{h.reason}</em></>}</li>)}</ul>{history.length > 5 && <button className="text-button" onClick={() => setAllHistory(!allHistory)}>{allHistory ? 'Show fewer' : `Show all ${history.length}`}</button>}</div>}
  </div>
}

export function PaginatedFindings({ findings, repos, pretty, relativeTime, canTriage, onTriage, onAssign, onRescan, stats, onBulkTriage, onExport, preset, onClearPreset, loadHistory }: {
  findings: Finding[]; repos: Repository[]; pretty: Pretty; relativeTime: RelativeTime; canTriage?: boolean; onTriage?: (finding: Finding, state: string, reason?: string) => void
  loadHistory?: (fingerprint: string) => Promise<HistoryItem[]>
  onAssign?: (finding: Finding, assignee: string) => void; onRescan?: (finding: Finding) => void; stats?: FindingStatsView | null
  onBulkTriage?: (findings: Finding[], state: string, reason?: string) => void; onExport?: () => void; preset?: FindingsPreset; onClearPreset?: () => void
}) {
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [views, setViews] = useState<Record<string, any>>(readViews); const [viewName, setViewName] = useState('')
  const severityLabel = (f: Finding) => `${pretty(f.severity)}${f.verified ? ' · verified' : ''}`
  const [filter, setFilter] = useState(''); const [stateFilter, setStateFilter] = useState(preset?.fingerprint ? '' : 'open'); const [scannerFilter, setScannerFilter] = useState('')
  const [severityFilter, setSeverityFilter] = useState(''); const [sort, setSort] = useState('newest'); const [page, setPage] = useState(1)
  const [expanded, setExpanded] = useState(preset?.fingerprint || ''); const pageSize = 10
  const matching = useMemo(() => {
    const result = findings.filter(f => (!stateFilter || f.state === stateFilter) && (!scannerFilter || f.scanner === scannerFilter) && (!severityFilter || f.severity === severityFilter)
      && (!preset?.repositoryId || f.repositoryId === preset.repositoryId) && (!preset?.executionId || f.executionId === preset.executionId) && (!preset?.fingerprint || f.fingerprint === preset.fingerprint)
      && `${f.repositoryId} ${f.detector} ${f.state} ${f.safePath} ${f.detail} ${f.fingerprint}`.toLowerCase().includes(filter.toLowerCase()))
    return [...result].sort((a, b) => sort === 'oldest' ? (a.lastSeen || '').localeCompare(b.lastSeen || '') : (b.lastSeen || '').localeCompare(a.lastSeen || ''))
  }, [findings, filter, stateFilter, scannerFilter, severityFilter, sort, preset])
  const pages = Math.max(1, Math.ceil(matching.length / pageSize)); const visible = matching.slice((page - 1) * pageSize, page * pageSize)
  const persistViews = (next: Record<string, any>) => { setViews(next); try { localStorage.setItem(VIEWS_KEY, JSON.stringify(next)) } catch { /* storage unavailable: views last for this session only */ } }
  const saveView = () => { const name = viewName.trim(); if (!name) return; persistViews({ ...views, [name]: { filter, stateFilter, scannerFilter, severityFilter, sort } }); setViewName('') }
  const applyView = (name: string) => { const v = views[name]; if (!v) return; setFilter(v.filter || ''); setStateFilter(v.stateFilter ?? 'open'); setScannerFilter(v.scannerFilter || ''); setSeverityFilter(v.severityFilter || ''); setSort(v.sort || 'newest'); setPage(1); setSelected(new Set()) }
  const deleteView = (name: string) => { const next = { ...views }; delete next[name]; persistViews(next) }
  const reset = (apply: () => void) => { apply(); setPage(1); setSelected(new Set()) }
  const toggleSelected = (fingerprint: string) => setSelected(current => { const next = new Set(current); if (next.has(fingerprint)) next.delete(fingerprint); else next.add(fingerprint); return next })
  const allVisibleSelected = visible.length > 0 && visible.every(f => selected.has(f.fingerprint))
  const bulk = (state: string, reason?: string) => { onBulkTriage?.(findings.filter(f => selected.has(f.fingerprint)), state, reason); setSelected(new Set()) }
  return <section className="panel feature-view findings-view">
    <div className="panel-head">
      <div><h2>Findings</h2><p className="feature-note">{matching.length} matching · expand a row for the exact issue, links to the forge, and triage (j / k move between rows).{stats && <> · <b className={stats.overdue ? 'critical' : ''}>{stats.overdue} overdue overall</b>{stats.mttrDays !== null && <> · mean time to resolve {stats.mttrDays}d across all findings (last 90d)</>}</>}</p></div>
      <div className="panel-actions">
        {onExport && <button className="secondary" onClick={onExport}>Export CSV</button>}
        <select aria-label="Sort findings" value={sort} onChange={e => reset(() => setSort(e.target.value))}><option value="newest">Newest first</option><option value="oldest">Oldest first</option></select>
      </div>
    </div>
    {canTriage && selected.size > 0 && <div className="bulk-bar" role="region" aria-label="Bulk triage">
      <b>{selected.size} selected</b>
      <TriageButtons current="open" onApply={bulk}/>
      <TriageButtons current="closed" onApply={bulk}/>
      <button className="text-button" onClick={() => setSelected(new Set())}>Clear selection</button>
    </div>}
    {(preset?.repositoryId || preset?.executionId || preset?.fingerprint) && <div className="filter-chips" aria-label="Active drill-down filters">
      {preset.repositoryId && <span className="chip">Repository: {repos.find(r => r.id === preset.repositoryId)?.name || preset.repositoryId}</span>}
      {preset.executionId && <span className="chip">Run {short(preset.executionId)}</span>}
      {preset.fingerprint && <span className="chip">Single finding</span>}
      <button className="text-button" onClick={onClearPreset}>Clear filter ✕</button>
    </div>}
    <div className="list-controls">
      <input className="view-filter" aria-label="Filter findings" placeholder="Filter repository, detector, path, detail, or fingerprint" value={filter} onChange={e => reset(() => setFilter(e.target.value))}/>
      <select aria-label="Filter by severity" value={severityFilter} onChange={e => reset(() => setSeverityFilter(e.target.value))}><option value="">All severities</option><option value="critical">Critical</option><option value="high">High</option><option value="medium">Medium</option><option value="low">Low</option></select>
      <select aria-label="Filter by state" value={stateFilter} onChange={e => reset(() => setStateFilter(e.target.value))}><option value="open">Open</option><option value="resolved">Resolved</option><option value="ignored">Ignored</option><option value="false_positive">False positive</option><option value="">All states</option></select>
      <select aria-label="Filter by scanner" value={scannerFilter} onChange={e => reset(() => setScannerFilter(e.target.value))}><option value="">All scanners</option>{[...new Set(['trufflehog', 'renovate', ...findings.map(f => f.scanner || '').filter(Boolean)])].map(id => <option key={id} value={id}>{pretty(id)}</option>)}</select>
    </div>
    <div className="saved-views">
      <select aria-label="Saved views" value="" onChange={e => applyView(e.target.value)}><option value="">Saved views…</option>{Object.keys(views).map(name => <option key={name} value={name}>{name}</option>)}</select>
      <input aria-label="View name" placeholder="Name the current filters" value={viewName} onChange={e => setViewName(e.target.value)}/>
      <button className="secondary" disabled={!viewName.trim()} onClick={saveView}>Save view</button>
      {Object.keys(views).length > 0 && <select aria-label="Delete a saved view" value="" onChange={e => deleteView(e.target.value)}><option value="">Delete view…</option>{Object.keys(views).map(name => <option key={name} value={name}>{name}</option>)}</select>}
    </div>
    <div className="feature-table finding-table" onKeyDown={e => {
      if ((e.key !== 'j' && e.key !== 'k') || (e.target as HTMLElement).closest('input,select,textarea')) return
      const rows = Array.from((e.currentTarget as HTMLElement).querySelectorAll('.finding-toggle')) as HTMLButtonElement[]; const at = rows.indexOf(document.activeElement as HTMLButtonElement)
      rows[Math.max(0, Math.min(rows.length - 1, at + (e.key === 'j' ? 1 : -1)))]?.focus()
    }}>
      <div className="feature-row feature-header"><span>{canTriage && <input type="checkbox" aria-label="Select all findings on this page" checked={allVisibleSelected} onChange={() => setSelected(allVisibleSelected ? new Set() : new Set(visible.map(f => f.fingerprint)))}/>}</span><span>Repository</span><span>Detector / location</span><span>Severity</span><span>State</span><span>Last seen</span></div>
      {visible.map(f => {
        const repo = repos.find(r => r.id === f.repositoryId); const isOpen = expanded === f.fingerprint; const toggle = () => setExpanded(isOpen ? '' : f.fingerprint)
        return <div className={`finding-record ${isOpen ? 'expanded' : ''}`} key={f.fingerprint}>
          <div className="feature-row finding-row" onClick={toggle}>
            <span onClick={e => e.stopPropagation()}>{canTriage && <input type="checkbox" aria-label={`Select ${findingTitle(f)} in ${f.repositoryId}`} checked={selected.has(f.fingerprint)} onChange={() => toggleSelected(f.fingerprint)}/>}</span>
            <span><RepositoryLink repo={repo} id={f.repositoryId}/></span>
            <span><button type="button" className="finding-toggle" aria-expanded={isOpen} onClick={e => { e.stopPropagation(); toggle() }}><strong>{findingTitle(f)}</strong>{(f.advisories || []).length > 0 && <span className="pill failed vuln-tag">{f.advisories![0].id.startsWith('CVE') ? 'CVE' : 'Vulnerable'}{f.advisories!.length > 1 ? ` +${f.advisories!.length - 1}` : ''}</span>}</button><small>{f.fileUrl ? <ExternalLink href={f.fileUrl} label={`Open ${f.safePath} on the forge`}>{f.safePath}{lineOf(f) ? `:${lineOf(f)}` : ''}</ExternalLink> : (f.safePath || 'Path withheld')}{f.pullRequestUrl && <> · <ExternalLink href={f.pullRequestUrl} label={`Open pull request #${f.pullRequestNumber}`}>PR #{f.pullRequestNumber}</ExternalLink></>}</small></span>
            <span className={f.severity || 'medium'}>{severityLabel(f)}</span>
            <span className={`pill ${f.state === 'open' ? 'failed' : 'success'}`}>{pretty(f.state)}</span>
            <span>{relativeTime(f.lastSeen)}<small>first seen {relativeTime(f.firstSeen)}{f.overdue && <b className="overdue"> · overdue</b>}{f.assignee && ` · ${f.assignee}`}</small></span>
          </div>
          {isOpen && <FindingDetails finding={f} repo={repo} pretty={pretty} relativeTime={relativeTime} canTriage={canTriage} onTriage={onTriage} onAssign={onAssign} onRescan={onRescan} loadHistory={loadHistory}/>}
        </div>
      })}
      {!visible.length && <div className="empty-state">No findings match these filters.</div>}
    </div>
    <Pager page={page} pages={pages} onChange={setPage}/>
  </section>
}

export const runDuration = (r: Run) => r.startedAt && r.finishedAt ? `${Math.max(1, Math.round((new Date(r.finishedAt).getTime() - new Date(r.startedAt).getTime()) / 1000))}s` : '—'

/** Pull requests and issues a scan opened on the forge; several pull requests collapse into one expandable chip. */
function RunLinks({ links }: { links: RunLink[] }) {
  const [open, setOpen] = useState(false)
  const pulls = links.filter(l => l.kind === 'pull_request'); const issues = links.filter(l => l.kind === 'issue')
  return <span className="run-links">
    {issues.map(l => <ExternalLink key={l.url} href={l.url} label={`Open dependency dashboard issue: ${l.title}`}>Dashboard issue</ExternalLink>)}
    {pulls.length === 1 && <ExternalLink href={pulls[0].url} label={`Open pull request ${pulls[0].title}`}>PR {pulls[0].title.split(' ')[0]}</ExternalLink>}
    {pulls.length > 1 && <button className="text-button" aria-expanded={open} onClick={() => setOpen(!open)}>{pulls.length} pull requests {open ? '▴' : '▾'}</button>}
    {open && <ul className="run-pr-list">{pulls.map(l => <li key={l.url}><ExternalLink href={l.url}>{l.title}</ExternalLink></li>)}</ul>}
  </span>
}

export function PaginatedRuns({ runs, repos, canRun, onRun, onCancel, onLogs, pretty, relativeTime, showRunButton = true, bare = false, onExport }: {
  runs: Run[]; repos: Repository[]; canRun: boolean; onRun: () => void; onCancel: (id: string) => void; onLogs: (run: Run) => void; pretty: Pretty; relativeTime: RelativeTime
  /** Hide the header "Run scan" button when the page already has one. */ showRunButton?: boolean
  /** Render only controls + table (for embedding inside another panel). */ bare?: boolean
  onExport?: () => void
}) {
  const { openFindings } = useContext(NavigationContext)
  const [filter, setFilter] = useState(''); const [sort, setSort] = useState('newest'); const [page, setPage] = useState(1); const pageSize = 10
  const matching = useMemo(() => {
    const result = runs.filter(r => `${r.repositoryId} ${r.status} ${r.scanner}`.toLowerCase().includes(filter.toLowerCase()))
    const when = (r: Run) => r.createdAt || r.startedAt || ''
    return [...result].sort((a, b) => sort === 'oldest' ? when(a).localeCompare(when(b)) : when(b).localeCompare(when(a)))
  }, [runs, filter, sort])
  const pages = Math.max(1, Math.ceil(matching.length / pageSize)); const visible = matching.slice((page - 1) * pageSize, page * pageSize)
  const body = <>
    <div className="list-controls">
      <input className="view-filter" aria-label="Filter runs" placeholder="Filter repository, scanner, or status" value={filter} onChange={e => { setFilter(e.target.value); setPage(1) }}/>
      <select aria-label="Sort runs" value={sort} onChange={e => { setSort(e.target.value); setPage(1) }}><option value="newest">Newest first</option><option value="oldest">Oldest first</option></select>
    </div>
    <div className="feature-table run-table">
      <div className="feature-row feature-header"><span>Scanner / repository</span><span>Mode</span><span>Status</span><span>Started / duration</span><span>Findings</span><span>Action</span></div>
      {visible.map(r => <div className="feature-row" key={r.id}>
        <strong>{pretty(r.scanner)}<small><RepositoryLink repo={repos.find(repo => repo.id === r.repositoryId)} id={r.repositoryId}/>{r.commitSHA && <> · <ExternalLink href={r.commitUrl} label={`Open commit ${short(r.commitSHA)} on the forge`}><code>{short(r.commitSHA)}</code></ExternalLink></>}</small></strong>
        <span>{r.scanner === 'renovate' ? '—' : pretty(r.mode)}</span>
        <span><span className={`pill ${r.status}`}>{pretty(r.status)}</span>{r.status === 'failed' && r.failureReason && <small className="failure-reason" title={r.failureReason}>{r.failureReason}</small>}</span>
        <span>{relativeTime(r.startedAt || r.createdAt)}<small>{runDuration(r)}{(r.attempt || 1) > 1 && <> · <span className="retried">{r.status === 'succeeded' ? `succeeded after ${r.attempt! - 1} ${r.attempt === 2 ? 'retry' : 'retries'}` : `${r.attempt} attempts`}</span></>}</small></span>
        <span>{r.status !== 'succeeded' ? '—' : r.outputInterpreted === false ? <span className="pill warning" title="This scanner's output format is 'none': Aegis cannot tell whether it found anything. Set an output format in the scanner settings.">Not interpreted</span> : r.findingCount ? <button className="text-button" onClick={() => openFindings({ executionId: r.id })}>{r.findingCount} →</button> : 0}</span>
        <span className="run-actions">
          <button className="text-button" onClick={() => onLogs(r)}>View logs</button>
          {r.links && r.links.length > 0 && <RunLinks links={r.links}/>}
          {canRun && ['queued', 'running', 'preparing'].includes(r.status) && <button className="text-button" onClick={() => onCancel(r.id)}>Cancel</button>}
        </span>
      </div>)}
      {!visible.length && <div className="empty-state">No executions match this filter.</div>}
    </div>
    <Pager page={page} pages={pages} onChange={setPage}/>
  </>
  if (bare) return body
  return <section className="feature-view"><div className="panel execution-list-panel">
    <div className="panel-head"><div><h2>Execution history</h2><p className="feature-note">Kubernetes scan Jobs: open the output, jump to the findings a run produced, or follow its pull requests.</p></div><div className="panel-actions">{onExport && <button className="secondary" onClick={onExport}>Export CSV</button>}{canRun && showRunButton && <button className="primary" onClick={onRun}>Run scan</button>}</div></div>
    {body}
  </div></section>
}
