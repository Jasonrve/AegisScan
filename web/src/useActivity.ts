import { useEffect, useState } from 'react'
import { loadActivity, type Activity } from './dataClient'

/** Latest run per repository/scanner (optionally for one repository), refreshed every 5 s while anything is in flight and every 15 s otherwise. */
export function useActivity(token: string | undefined, repositoryId?: string, enabled = true) {
  const [activity, setActivity] = useState<Activity | null>(null)
  const [failed, setFailed] = useState(false)
  const busy = !!activity?.active.length
  useEffect(() => {
    if (!enabled) return undefined
    let cancelled = false
    const load = () => loadActivity(fetch, token, repositoryId).then(a => { if (!cancelled) { setActivity(a); setFailed(false) } }).catch(() => { if (!cancelled) setFailed(true) })
    load()
    const timer = setInterval(load, busy ? 5000 : 15000)
    return () => { cancelled = true; clearInterval(timer) }
  }, [token, repositoryId, enabled, busy])
  return { activity, failed }
}
