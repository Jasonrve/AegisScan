import { useEffect, useState } from 'react'
import { loadFixes, type Fix, type Fixes } from './dataClient'
import { SEVERITIES } from './activityGraph'

/** Ranked actions for a set of repositories (null = all), refreshed every minute. */
export function useFixes(token: string | undefined, ids: string[] | null, enabled = true) {
  const [data, setData] = useState<Fixes | null>(null)
  const [failed, setFailed] = useState(false)
  const key = ids?.join('\n') ?? ''
  useEffect(() => {
    if (!enabled) return undefined
    let cancelled = false
    setData(null)
    const load = () => loadFixes(ids, fetch, token).then(d => { if (!cancelled) { setData(d); setFailed(false) } }).catch(() => { if (!cancelled) setFailed(true) })
    load()
    const timer = setInterval(load, 60000)
    return () => { cancelled = true; clearInterval(timer) }
  }, [token, key, enabled])
  return { data, failed }
}

const KIND: Record<Fix['kind'], string> = { image: 'Base image', package: 'Dependency', secret: 'Secret', update: 'Update', issue: 'Code issue' }

function FixRow({ fix, showRepo, onOpen }: { key?: string; fix: Fix; showRepo: boolean; onOpen: (fix: Fix) => void }) {
  const mono = fix.kind === 'image' || fix.kind === 'package'
  return <li className={`fix sev-${fix.severity}`}>
    <div className="fix-main">
      <div className="fix-title">{mono ? <code>{fix.title}</code> : <b>{fix.title}</b>}<span className="fn-chip">{KIND[fix.kind]}</span>{fix.verified && <span className="pill failed">Live</span>}
        {showRepo && <button className="text-button fix-repo" onClick={() => onOpen(fix)}>{fix.repositoryName}</button>}</div>
      <div className="fix-where">{fix.where}</div>
      <div className="fix-action">{fix.action}</div>
    </div>
    <div className="fix-counts" aria-label={SEVERITIES.map(s => `${fix[s]} ${s}`).join(', ')}>
      {SEVERITIES.filter(s => fix[s] > 0).map(s => <span key={s}><i className={`sevdot ${s}`}/>{fix[s].toLocaleString()} {s}</span>)}
    </div>
  </li>
}

/** The answer to "what do I fix, and where": one row per action (bump this image, upgrade this package, rotate this secret…). */
export default function FixList({ data, failed, limit = 8, showRepo = false, onOpen, empty = 'Nothing to fix.' }: { data: Fixes | null; failed: boolean; limit?: number; showRepo?: boolean; onOpen: (fix: Fix) => void; empty?: string }) {
  if (!data) return <div className="empty-state"><span>{failed ? 'Could not load the fix list.' : 'Working out what to fix…'}</span></div>
  if (!data.fixes.length) return <div className="empty-state"><span>{empty}</span></div>
  return <>
    <ul className="fix-list">{data.fixes.slice(0, limit).map((f, i) => <FixRow key={`${f.repositoryId}${f.kind}${f.title}${f.where}${i}`} fix={f} showRepo={showRepo} onOpen={onOpen}/>)}</ul>
    {data.total > Math.min(limit, data.fixes.length) && <p className="feature-note">Showing the {Math.min(limit, data.fixes.length)} most important of {data.total.toLocaleString()} actions.</p>}
  </>
}
