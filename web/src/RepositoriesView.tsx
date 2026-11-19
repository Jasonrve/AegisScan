import { useContext, useEffect, useMemo, useState } from 'react'
import { loadRepositoryRisk, type Repository, type RepositoryRisk } from './dataClient'
import { SEVERITIES } from './activityGraph'
import { NavigationContext, Pager, RepositoryLink } from './PaginatedViews'
import { StarButton, useScope } from './scope'

const healthOf = (r: Repository) => r.status === 'Blocked' ? 'blocked' : r.health || 'healthy'
const PAGE = 15

function useRisk(token: string | undefined, enabled: boolean) {
  const [risk, setRisk] = useState<Map<string, RepositoryRisk>>(new Map())
  useEffect(() => {
    if (!enabled) return undefined
    let cancelled = false
    const load = () => loadRepositoryRisk(fetch, token).then(rows => { if (!cancelled) setRisk(new Map(rows.map(r => [r.id, r]))) }).catch(() => {})
    load()
    const timer = setInterval(load, 30000)
    return () => { cancelled = true; clearInterval(timer) }
  }, [token, enabled])
  return risk
}

const total = (r?: RepositoryRisk) => r ? r.critical + r.high + r.medium + r.low : 0

/** Repositories with what matters per row: how risky it is, when it was last scanned and what state it is in. Highest risk first; never-scanned repositories stay out of the way once there are scanned ones. */
export default function RepositoriesView({ repos, token, enabled, initialFilter, canRun, canConfigure, onRun, onAdd, onDiscovery, onRemoveRepo, relativeTime }: { repos: Repository[]; token?: string; enabled: boolean; initialFilter?: string; canRun: boolean; canConfigure: boolean; onRun: (id: string) => void; onAdd: () => void; onDiscovery: () => void; onRemoveRepo: (repo: Repository) => void; relativeTime: (value?: string) => string }) {
  const { navigate } = useContext(NavigationContext)
  const scope = useScope()
  const risk = useRisk(token, enabled)
  const [filter, setFilter] = useState(initialFilter || '')
  const [health, setHealth] = useState('')
  const [sort, setSort] = useState('risk')
  const [showUnscanned, setShowUnscanned] = useState(false)
  const [page, setPage] = useState(1)
  const scoped = useMemo(() => repos.filter(r => !scope.ids || scope.has(r.id)), [repos, scope.ids, scope.watched])
  const matching = scoped.filter(r => r.name.toLowerCase().includes(filter.toLowerCase()) && (!health || healthOf(r) === health))
  const unscanned = matching.filter(r => healthOf(r) === 'never-scanned')
  // Hidden only when it helps: there are scanned repositories, a lot of them overall, and the user is not searching or filtering.
  const hide = !showUnscanned && !filter && !health && unscanned.length > 0 && unscanned.length < matching.length && matching.length > PAGE
  const visible = (hide ? matching.filter(r => healthOf(r) !== 'never-scanned') : matching).sort((a, b) => {
    const ra = risk.get(a.id), rb = risk.get(b.id)
    if (sort === 'name') return a.name.localeCompare(b.name)
    if (sort === 'findings') return total(rb) - total(ra) || b.activeFindingCount - a.activeFindingCount
    if (sort === 'health') return healthOf(a).localeCompare(healthOf(b))
    const urgent = (r: Repository, x?: RepositoryRisk) => Number(r.status === 'Blocked' || (x?.verified ?? 0) > 0)
    return urgent(b, rb) - urgent(a, ra) || (rb?.critical ?? 0) - (ra?.critical ?? 0) || (rb?.high ?? 0) - (ra?.high ?? 0) || (rb?.medium ?? 0) - (ra?.medium ?? 0) || a.name.localeCompare(b.name)
  })
  const pages = Math.max(1, Math.ceil(visible.length / PAGE)); const paged = visible.slice((page - 1) * PAGE, page * PAGE)
  const max = Math.max(1, ...visible.map(r => total(risk.get(r.id))))
  return <section className="panel feature-view">
    <div className="panel-head"><div><h2>Managed repositories</h2><p className="feature-note">Highest risk first. Click a repository to see what to fix; star the ones you own to scope the whole app to them.</p></div>
      {canConfigure && <div className="panel-actions"><button className="secondary" onClick={onDiscovery}>Add discovery</button><button className="primary" onClick={onAdd}>Add repository →</button></div>}</div>
    <div className="list-controls">
      <input className="view-filter" aria-label="Filter repositories" placeholder="Filter repositories" value={filter} onChange={e => { setFilter(e.target.value); setPage(1) }}/>
      <select aria-label="Filter by health" value={health} onChange={e => { setHealth(e.target.value); setPage(1) }}><option value="">All health</option><option value="healthy">Healthy</option><option value="failing">Failing</option><option value="never-scanned">Never scanned</option><option value="blocked">Blocked</option></select>
      <select aria-label="Sort repositories" value={sort} onChange={e => setSort(e.target.value)}><option value="risk">Highest risk</option><option value="name">Name</option><option value="findings">Most findings</option><option value="health">Health</option></select>
    </div>
    <div className="feature-table repos-table">
      <div className="feature-row repo-row feature-header"><span/><span>Repository</span><span>Open findings by severity</span><span>Last scan</span><span>Status / actions</span></div>
      {paged.map(r => { const x = risk.get(r.id); const t = total(x); const pill = r.status === 'Blocked' || r.health === 'failing' ? 'failed' : r.health === 'never-scanned' ? 'canceled' : 'success'
        return <div className="feature-row repo-row" key={r.id}>
          <StarButton id={r.id} name={r.name}/>
          <span><RepositoryLink repo={r} id={r.id}/></span>
          <span className="repo-risk">{x ? (t || x.verified ? <button className="text-button repo-risk-btn" onClick={() => navigate(r.id)} aria-label={`${r.name}: ${t} open findings. See what to fix`}>
              <span className="fn-bar" style={{ width: `${Math.max(8, (t / max) * 100)}%` }}>{SEVERITIES.filter(s => x[s]).map(s => <i key={s} className={s} style={{ flexGrow: x[s] }}/>)}</span>
              <small>{x.verified > 0 && <b className="verified">{x.verified} live secret{x.verified === 1 ? '' : 's'} · </b>}{SEVERITIES.filter(s => x[s]).slice(0, 2).map(s => `${x[s].toLocaleString()} ${s}`).join(' · ') || `${t.toLocaleString()} findings`}</small></button> : <small className="fn-clean">{r.health === 'never-scanned' ? '—' : 'clean'}</small>) : <small>{r.activeFindingCount || '—'}</small>}</span>
          <span>{x?.lastScanAt ? relativeTime(x.lastScanAt) : '—'}</span>
          <span className="repo-actions"><b className={`pill ${pill}`}>{r.status === 'Blocked' ? 'Blocked' : r.health === 'failing' ? 'Failing' : r.health === 'never-scanned' ? 'Never scanned' : 'Ready'}</b>{canRun && <button className="text-button" onClick={() => onRun(r.id)}>Scan now</button>}{canConfigure && <button className="text-button danger" onClick={() => onRemoveRepo(r)}>Remove</button>}</span>
        </div> })}
      {!visible.length && <div className="empty-state"><span>{scope.ids && !scoped.length ? 'None of your starred repositories exist any more.' : 'No repositories match this filter.'}</span></div>}
    </div>
    {hide && <p className="feature-note">{unscanned.length} never-scanned repositories are hidden. <button className="text-button" onClick={() => setShowUnscanned(true)}>Show them</button></p>}
    {showUnscanned && !filter && !health && unscanned.length > 0 && <p className="feature-note"><button className="text-button" onClick={() => setShowUnscanned(false)}>Hide never-scanned repositories</button></p>}
    <Pager page={page} pages={pages} onChange={setPage}/>
  </section>
}
