import { useMemo } from 'react'
import type { Execution } from './dataClient'

const PER_LANE = 30
const PROGRESS: Record<string, number> = { succeeded: 100, failed: 100, canceled: 100, timed_out: 100, running: 60 }

function duration(run: Execution) {
  if (!run.startedAt) return ''
  const s = Math.max(0, Math.round(((run.finishedAt ? Date.parse(run.finishedAt) : Date.now()) - Date.parse(run.startedAt)) / 1000))
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`
}

/** One swim lane per scanner, one progress-style bar per scan, oldest on the left. Click a bar for the scan's log. */
export default function ScanTimelinePanel({ runs, onLogs, relativeTime, pretty }: { runs: Execution[]; onLogs: (run: Execution) => void; relativeTime: (value?: string) => string; pretty: (value: any) => string }) {
  const lanes = useMemo(() => {
    const byScanner = new Map<string, Execution[]>()
    for (const run of runs) byScanner.set(run.scanner, [...(byScanner.get(run.scanner) || []), run])
    return [...byScanner.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([scanner, list]) => ({
      scanner,
      runs: list.sort((a, b) => Date.parse(a.createdAt || '') - Date.parse(b.createdAt || '')).slice(-PER_LANE),
    }))
  }, [runs])
  return <div className="panel repo-timeline-panel">
    <div className="panel-head"><div><h2>Scan timeline</h2><p className="feature-note">Every scan of this repository, one lane per scanner, oldest to newest. Click a bar for its log.</p></div></div>
    {!lanes.length ? <div className="empty-state"><span>No scans yet.</span></div> : <>
      {lanes.map(lane => <div className="tl-lane" key={lane.scanner}>
        <div className="tl-name">{pretty(lane.scanner)}</div>
        <div className="tl-track">{lane.runs.map(run => {
          const when = run.finishedAt || run.startedAt || run.createdAt
          const detail = run.status === 'succeeded' ? (run.outputInterpreted === false ? 'not interpreted' : `${run.findingCount} finding${run.findingCount === 1 ? '' : 's'}`) : pretty(run.status)
          return <button type="button" key={run.id} className={`tl-bar ${run.status}`} onClick={() => onLogs(run)}
            title={`${pretty(lane.scanner)} · ${run.status} · ${when ? new Date(when).toLocaleString() : ''}${duration(run) ? ` · ${duration(run)}` : ''}${run.attempt > 1 ? ` · attempt ${run.attempt}` : ''}`}>
            <span className="tl-fill" style={{ width: `${PROGRESS[run.status] ?? 0}%` }}/>
            <span className="tl-label"><b>{detail}</b><small>{[relativeTime(when), duration(run)].filter(Boolean).join(' · ')}</small></span>
          </button>
        })}</div>
      </div>)}
      <div className="flow-legend"><span><i className="tl-key succeeded"/>succeeded</span><span><i className="tl-key running"/>running</span><span><i className="tl-key failed"/>failed</span><span><i className="tl-key queued"/>queued</span><span><i className="tl-key canceled"/>canceled</span></div>
    </>}
  </div>
}
