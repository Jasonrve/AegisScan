import { useContext, useEffect, useMemo, useState } from 'react'
import { loadDashboard, type Dashboard, type DashboardCve, type Finding, type Repository, type Execution } from './dataClient'
import { SEVERITIES } from './activityGraph'
import { ExternalLink, NavigationContext } from './PaginatedViews'
import FixList, { useFixes } from './FixList'
import { useScope } from './scope'

const DAYS: Record<string, number> = { 'Last 7 days': 7, 'Last 30 days': 30, 'Last 90 days': 90 }
const cveUrl = (id: string) => id.startsWith('GHSA-') ? `https://github.com/advisories/${id}` : `https://nvd.nist.gov/vuln/detail/${id}`
const n = (v: number) => v.toLocaleString()

/** Loaded once per range/scope and refreshed every minute (the aggregate scans every open finding, so it is not part of the 5 s list polling). */
function useDashboard(token: string | undefined, days: number, enabled: boolean, ids: string[] | null) {
  const [data, setData] = useState<Dashboard | null>(null)
  const [failed, setFailed] = useState(false)
  const key = ids?.join('\n') ?? ''
  useEffect(() => {
    if (!enabled) return undefined
    let cancelled = false
    const load = () => loadDashboard(days, fetch, token, ids).then(d => { if (!cancelled) { setData(d); setFailed(false) } }).catch(() => { if (!cancelled) setFailed(true) })
    load()
    const timer = setInterval(load, 60000)
    return () => { cancelled = true; clearInterval(timer) }
  }, [token, days, enabled, key])
  return { data, failed }
}

/** Fixture/offline mode: the same shape, derived from the findings already in memory. */
function localDashboard(findings: Finding[], repos: Repository[], days: number): Dashboard {
  const open = findings.filter(f => f.state === 'open')
  const by = (s: string) => open.filter(f => f.severity === s).length
  const rows = repos.map(r => { const own = open.filter(f => f.repositoryId === r.id); const c = (s: string) => own.filter(f => f.severity === s).length; return { id: r.id, name: r.name, blocked: r.status === 'Blocked', verified: own.filter(f => f.verified).length, critical: c('critical'), high: c('high'), medium: c('medium'), low: c('low'), categories: own.length ? { findings: own.length } : {} } })
    .filter(r => r.critical + r.high + r.medium + r.low > 0 || r.blocked).sort((a, b) => b.critical - a.critical || b.high - a.high).slice(0, 10)
  return { days, totals: { critical: by('critical'), high: by('high'), medium: by('medium'), low: by('low'), verifiedSecrets: open.filter(f => f.verified).length, reposWithCritical: new Set(open.filter(f => f.severity === 'critical').map(f => f.repositoryId)).size, cvesTotal: 0, newCves: 0 }, repos: rows, newCves: [], widespread: [], generatedAt: '' }
}

function Tile({ label, value, note, tone, onClick }: { label: string; value: number; note: string; tone: string; onClick: () => void }) {
  return <button className={`dash-tile ${tone}`} onClick={onClick} aria-label={`${label}: ${value}. ${note}`}><span>{label}</span><strong>{n(value)}</strong><small>{note}</small></button>
}

function RepoRow({ repo, max, onOpen }: { key?: string; repo: Dashboard['repos'][number]; max: number; onOpen: () => void }) {
  const total = repo.critical + repo.high + repo.medium + repo.low
  const chips = Object.entries(repo.categories).sort((a, b) => b[1] - a[1])
  return <li className="dash-repo">
    <div className="dash-repo-name"><button className="text-button" onClick={onOpen}>{repo.name}</button>{repo.blocked && <span className="pill failed">Blocked</span>}{repo.verified > 0 && <span className="pill failed">{repo.verified} live secret{repo.verified === 1 ? '' : 's'}</span>}</div>
    <div className="dash-repo-bar" role="img" aria-label={SEVERITIES.map(s => `${repo[s]} ${s}`).join(', ')}><div className="fn-bar" style={{ width: `${Math.max(6, (total / max) * 100)}%` }}>{SEVERITIES.filter(s => repo[s]).map(s => <i key={s} className={s} style={{ flexGrow: repo[s] }}/>)}</div></div>
    <div className="dash-repo-meta"><b>{n(total)}</b>{SEVERITIES.filter(s => repo[s]).slice(0, 2).map(s => <span key={s}><i className={`sevdot ${s}`}/>{n(repo[s])} {s}</span>)}{chips.slice(0, 2).map(([k, v]) => <span key={k} className="fn-chip">{k} {n(v)}</span>)}</div>
  </li>
}

function CveItem({ cve, names, onOpenRepo }: { key?: string; cve: DashboardCve; names: Map<string, string>; onOpenRepo: (id: string) => void }) {
  const [open, setOpen] = useState(false)
  return <li>
    <div className="dash-cve-row">
      <span className={`sev ${cve.severity}`}>{cve.severity}</span>
      <div><ExternalLink href={cveUrl(cve.id)} label={`Open advisory ${cve.id}`}>{cve.id}</ExternalLink><small>{cve.package}</small></div>
      {cve.fixAvailable ? <span className="fn-chip fix-chip">fix available</span> : <span className="fn-chip">no fix yet</span>}
      <button className="text-button dash-cve-repos" aria-expanded={open} onClick={() => setOpen(!open)}>{cve.repos} repo{cve.repos === 1 ? '' : 's'} {open ? '▴' : '▾'}</button>
    </div>
    {open && <div className="dash-cve-where">{cve.repoIds.map(id => <button key={id} className="text-button" onClick={() => onOpenRepo(id)}>{names.get(id) || id}</button>)}{cve.repos > cve.repoIds.length && <small>and {cve.repos - cve.repoIds.length} more</small>}</div>}
  </li>
}

export default function DashboardView({ repos, runs, findings, token, enabled, range }: { repos: Repository[]; runs: Execution[]; findings: Finding[]; token?: string; enabled: boolean; range: string }) {
  const { navigate, openFindings } = useContext(NavigationContext)
  const scope = useScope()
  const days = DAYS[range] ?? 7
  const { data: remote, failed } = useDashboard(token, days, enabled, scope.ids)
  const { data: fixes, failed: fixesFailed } = useFixes(token, scope.ids, enabled)
  const inScope = (id: string) => !scope.ids || scope.has(id)
  const data = useMemo(() => enabled ? remote : localDashboard(findings, repos.filter(r => inScope(r.id)), days), [enabled, remote, findings, repos, days, scope.ids])
  const names = useMemo(() => new Map(repos.map(r => [r.id, r.name])), [repos])
  const [picked, setTab] = useState<'new' | 'wide' | null>(null)
  const since = Date.now() - days * 86400000
  const scoped = runs.filter(r => inScope(r.repositoryId))
  const failedRuns = scoped.filter(r => ['failed', 'timed_out'].includes(r.status) && new Date(r.createdAt || r.startedAt || 0).getTime() >= since).length
  const running = scoped.filter(r => ['queued', 'preparing', 'running'].includes(r.status)).length
  if (!data) return <section className="panel feature-view"><div className="empty-state"><span>{failed ? 'Could not load the dashboard.' : 'Loading dashboard…'}</span></div></section>
  const t = data.totals
  const tab = picked ?? (data.newCves.length ? 'new' : 'wide') // nothing new: open on what is most widespread instead of an empty list
  const max = Math.max(1, ...data.repos.map(r => r.critical + r.high + r.medium + r.low))
  const scopeNote = scope.ids ? `Your ${scope.ids.length} repositor${scope.ids.length === 1 ? 'y' : 'ies'}` : 'All repositories'
  const cves = tab === 'new' ? data.newCves : data.widespread
  return <section className="dash">
    <p className="feature-note dash-scope">{scopeNote}</p>
    <div className="dash-tiles">
      <Tile label="Critical open" value={t.critical} note={`in ${t.reposWithCritical} repositor${t.reposWithCritical === 1 ? 'y' : 'ies'}`} tone="critical" onClick={() => openFindings({})}/>
      <Tile label="High open" value={t.high} note="across all scanners" tone="high" onClick={() => openFindings({})}/>
      <Tile label="Live secrets" value={t.verifiedSecrets} note="verified credentials" tone="critical" onClick={() => openFindings({})}/>
      <Tile label={`New CVEs · ${days}d`} value={t.newCves} note={`${n(t.cvesTotal)} CVEs open in total`} tone="violet" onClick={() => setTab('new')}/>
      <Tile label="Failed runs" value={failedRuns} note={`in the last ${days} days`} tone="neutral" onClick={() => navigate('Scans')}/>
    </div>
    <div className="dash-main">
      {enabled && <div className="panel">
        <div className="panel-head"><div><h2>Top fixes</h2><p className="feature-note">The changes that clear the most risk, live secrets first. Click a repository to see all of its fixes.</p></div></div>
        <FixList data={fixes} failed={fixesFailed} limit={8} showRepo onOpen={f => navigate(f.repositoryId)} empty="Nothing to fix. Nice."/>
      </div>}
      <div className="panel">
        <div className="panel-head"><div><h2>Riskiest repositories</h2><p className="feature-note">Blocked repositories and live secrets first, then critical and high findings.</p></div></div>
        {data.repos.length ? <ul className="dash-repos">{data.repos.slice(0, 6).map(r => <RepoRow key={r.id} repo={r} max={max} onOpen={() => navigate(r.id)}/>)}</ul> : <div className="empty-state"><span>No open findings. Nothing to report.</span></div>}
      </div>
    </div>
    <div className="panel">
      <div className="panel-head"><div><h2>Vulnerabilities</h2><p className="feature-note">{tab === 'new' ? 'CVEs first found on a repository after its baseline scan.' : 'Critical and high CVEs present in the most repositories.'}</p></div>
        <div className="dash-tabs" role="tablist"><button role="tab" aria-selected={tab === 'new'} className={tab === 'new' ? 'active' : ''} onClick={() => setTab('new')}>New in {days} days</button><button role="tab" aria-selected={tab === 'wide'} className={tab === 'wide' ? 'active' : ''} onClick={() => setTab('wide')}>Most widespread</button></div></div>
      {cves.length ? <ul className="dash-cves">{cves.map(c => <CveItem key={c.id} cve={c} names={names} onOpenRepo={navigate}/>)}</ul>
        : <div className="empty-state"><span>{tab === 'new' ? `No new CVEs. ${n(t.cvesTotal)} were found on first scans; later scans will list new ones here.` : 'No critical or high CVEs.'}</span></div>}
    </div>
    <div className="panel dash-health"><span><b>{running}</b> scan{running === 1 ? '' : 's'} running or queued</span><span><b>{failedRuns}</b> failed in {days} days</span><button className="text-button" onClick={() => navigate('Activity')}>See what is running where →</button></div>
  </section>
}
