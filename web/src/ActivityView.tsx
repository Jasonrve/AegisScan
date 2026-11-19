import { Fragment, useContext, useMemo, useState } from 'react'
import type { ActivityRun, Repository } from './dataClient'
import { IN_FLIGHT, isRetryPending, statusLabel } from './activityGraph'
import { NavigationContext } from './PaginatedViews'
import { useActivity } from './useActivity'

const tone = (run?: ActivityRun) => !run ? 'never' : isRetryPending(run) ? 'queued' : run.status
const elapsed = (run: ActivityRun) => { if (!run.startedAt) return 'waiting'; const s = Math.max(0, Math.round((Date.now() - new Date(run.startedAt).getTime()) / 1000)); return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s` }

/** Fleet view: what is running right now, and the latest run of every scanner on every repository. */
export default function ActivityView({ repos, token, enabled, relativeTime, pretty }: { repos: Repository[]; token?: string; enabled: boolean; relativeTime: (value?: string) => string; pretty: (value: any) => string }) {
  const { navigate, openRun } = useContext(NavigationContext)
  const { activity, failed } = useActivity(token, undefined, enabled)
  const [query, setQuery] = useState('')
  const [show, setShow] = useState('all')
  const latest = useMemo(() => new Map(activity?.latest.map(r => [`${r.repositoryId}\n${r.scanner}`, r])), [activity])
  const open = useMemo(() => new Map(activity?.open.map(o => [`${o.repositoryId}\n${o.scanner}`, o.count])), [activity])
  if (!enabled) return <section className="panel feature-view"><div className="empty-state"><span>Activity is shown when connected to the live API.</span></div></section>
  if (!activity) return <section className="panel feature-view"><div className="empty-state"><span>{failed ? 'Could not load scan activity.' : 'Loading scan activity…'}</span></div></section>
  const names = new Map(repos.map(r => [r.id, r.name]))
  const q = query.trim().toLowerCase()
  const rows = repos.filter(r => (!q || r.name.toLowerCase().includes(q)) && (show === 'all' || activity.scanners.some(s => { const run = latest.get(`${r.id}\n${s.id}`); return show === 'active' ? !!run && IN_FLIGHT.includes(run.status) : run?.status === 'failed' })))
  const running = activity.active.filter(r => r.status !== 'queued').length
  const retrying = activity.active.filter(r => isRetryPending(r)).length
  const queued = activity.active.length - running - retrying
  return <section className="feature-view activity-view">
    <div className="panel">
      <div className="panel-head"><div><h2>Running now</h2><p className="feature-note">{running} running · {queued} queued · {retrying} waiting to retry. Updates every few seconds while scans are in flight.</p></div></div>
      {activity.active.length ? <div className="activity-strip">{activity.active.map(run => <button key={run.id} className={`activity-card ${tone(run)}`} onClick={() => openRun(run.id)} title="Open the run log">
        <b>{pretty(run.scanner)}</b><span>→ {names.get(run.repositoryId) || run.repositoryId}</span>
        <small>{statusLabel(run)} · {isRetryPending(run) ? `retries ${relativeTime(run.notBefore).replace('just now', 'soon')}` : elapsed(run)}{run.attempt > 1 ? ` · attempt ${run.attempt}` : ''}</small>
        {run.jobName && <small className="mono">{run.jobName}</small>}
      </button>)}</div> : <div className="empty-state"><span>Nothing is running.</span></div>}
    </div>
    <div className="panel">
      <div className="panel-head">
        <div><h2>Repositories × scanners</h2><p className="feature-note">The latest run of each scanner on each repository, with its open findings. Click a repository for its scan flow, or a cell for the run log.</p></div>
        <div className="activity-filters"><input className="view-filter" aria-label="Filter repositories" placeholder="Filter repositories" value={query} onChange={e => setQuery(e.target.value)}/>
          <select aria-label="Show" value={show} onChange={e => setShow(e.target.value)}><option value="all">All</option><option value="active">Running or queued</option><option value="failed">Failed</option></select></div>
      </div>
      <div className="activity-scroll"><div className="activity-matrix" style={{ gridTemplateColumns: `minmax(180px,1.4fr) repeat(${activity.scanners.length}, minmax(130px,1fr))` }}>
        <div className="feature-header">Repository</div>{activity.scanners.map(s => <div key={s.id} className="feature-header">{s.name}</div>)}
        {rows.map(r => <Fragment key={r.id}><button className="text-button activity-repo" onClick={() => navigate(r.id)}>{r.name}</button>
          {activity.scanners.map(s => { const run = latest.get(`${r.id}\n${s.id}`); const n = open.get(`${r.id}\n${s.id}`) ?? 0
            return run ? <button key={`${r.id}${s.id}`} className={`activity-cell ${tone(run)}`} onClick={() => openRun(run.id)}>
              <i/><span>{IN_FLIGHT.includes(run.status) ? statusLabel(run) : !run.outputInterpreted && run.status === 'succeeded' ? 'not interpreted' : run.status === 'succeeded' ? `${n} open` : statusLabel(run)}</span><small>{relativeTime(run.finishedAt || run.startedAt || run.createdAt)}</small></button>
              : <span key={`${r.id}${s.id}`} className="activity-cell never"><i/><span>—</span></span> })}</Fragment>)}
      </div>
      </div>{!rows.length && <div className="empty-state"><span>No repositories match.</span></div>}
    </div>
  </section>
}
