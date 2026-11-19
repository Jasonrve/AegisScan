import { useContext, useEffect, useState } from 'react'
import { loadRepositoryLanguages, type Repository, type Execution, type Finding } from './dataClient'
import SbomPanel from './SbomPanel'
import ScanFlowPanel from './ScanFlowPanel'
import { repoHref, forgeName, ExternalLink, NavigationContext, ProviderIcon, PaginatedRuns } from './PaginatedViews'

const LANGUAGE_COLORS: Record<string, string> = {
  TypeScript: '#3178c6', JavaScript: '#f1e05a', Python: '#3572A5', Go: '#00ADD8', 'C#': '#178600',
  Java: '#b07219', Ruby: '#701516', PHP: '#4F5D95', Rust: '#dea584', HTML: '#e34c26', CSS: '#563d7c',
  Shell: '#89e051', Dockerfile: '#384d54', C: '#555555', 'C++': '#f34b7d', Kotlin: '#A97BFF', Swift: '#F05138',
  SCSS: '#c6538c', Vue: '#41b883', Dart: '#00B4AB', YAML: '#cb171e',
}
function languageColor(name: string) {
  if (LANGUAGE_COLORS[name]) return LANGUAGE_COLORS[name]
  let hash = 0
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) >>> 0
  return `hsl(${hash % 360} 55% 55%)`
}

function LanguageBar({ languages }: { languages: Record<string, number> | null }) {
  if (languages === null) return <div className="empty-state"><span>Loading language breakdown…</span></div>
  const total = Object.values(languages).reduce((a, b) => a + b, 0)
  if (!total) return <div className="empty-state"><span>No language data available (only GitHub-hosted repositories report languages).</span></div>
  const sorted = Object.entries(languages).sort((a, b) => b[1] - a[1])
  return <div className="language-breakdown">
    <div className="language-bar">{sorted.map(([name, bytes]) => <span key={name} style={{ width: `${(bytes / total) * 100}%`, background: languageColor(name) }} title={`${name} · ${((bytes / total) * 100).toFixed(1)}%`}/>)}</div>
    <div className="language-legend">{sorted.map(([name, bytes]) => <span key={name}><i style={{ background: languageColor(name) }}/>{name}<b>{((bytes / total) * 100).toFixed(1)}%</b></span>)}</div>
  </div>
}

/** Derived, client-side security posture score — no backend field backs this. Open verified findings and a blocked status dominate; recent failures nudge it down. */
function computeScorecard(repo: Repository, openFindings: Finding[], repoRuns: Execution[]) {
  const verified = openFindings.filter(f => f.verified).length
  const isSecret = (f: { scanner?: string; category?: string }) => f.category ? f.category === 'secrets' : f.scanner === 'trufflehog'
  const others = openFindings.filter(f => !f.verified && isSecret(f)).length
  const updates = openFindings.filter(f => f.scanner === 'renovate').length
  const issues = openFindings.filter(f => f.scanner !== 'renovate' && !isSecret(f)).length
  const recent = repoRuns.slice(0, 10)
  const failed = recent.filter(r => ['failed', 'timed_out'].includes(r.status)).length
  let score = 100 - verified * 25 - others * 8 - Math.min(updates, 10) * 2 - Math.min(issues, 10) * 5 - failed * 5
  if (repo.status === 'Blocked') score = Math.min(score, 40)
  score = Math.max(0, Math.min(100, score))
  const grade = score >= 90 ? 'A' : score >= 75 ? 'B' : score >= 60 ? 'C' : score >= 40 ? 'D' : 'F'
  const tone = score >= 75 ? 'green' : score >= 40 ? 'amber' : 'red'
  const successRate = recent.length ? Math.round((recent.filter(r => r.status === 'succeeded').length / recent.length) * 100) : null
  return { score, grade, tone, verified, others, updates, issues, successRate, failed, recentCount: recent.length }
}

export default function RepositoryDetailView({ repo, runs, findings, token, canRun, canUnblock, onUnblock, onRun, onCancel, onLogs, pretty, relativeTime }: {
  repo: Repository
  runs: Execution[]
  findings: Finding[]
  token?: string
  canRun: boolean
  canUnblock?: boolean
  onUnblock?: (repo: Repository) => void
  onRun: (repositoryId?: string) => void
  onCancel: (id: string) => Promise<void>
  onLogs: (run: Execution) => void
  pretty: (value: any) => string
  relativeTime: (value?: string) => string
}) {
  const { openFindings } = useContext(NavigationContext)
  const [languages, setLanguages] = useState<Record<string, number> | null>(null)
  useEffect(() => {
    let cancelled = false
    setLanguages(null)
    loadRepositoryLanguages(repo.id, fetch, token).then(result => { if (!cancelled) setLanguages(result) }).catch(() => { if (!cancelled) setLanguages({}) })
    return () => { cancelled = true }
  }, [repo.id, token])

  const repoRuns = runs.filter(r => r.repositoryId === repo.id)
  const open = findings.filter(f => f.repositoryId === repo.id && f.state === 'open')
  const card = computeScorecard(repo, open, repoRuns)
  const href = repoHref(repo)
  const forge = forgeName(repo.provider)
  const lastScan = repoRuns.find(r => r.status === 'succeeded')
  const commitUrl = href && repo.latestCommitSHA ? `${href}/${repo.provider === 'gitlab' ? '-/' : ''}commit/${repo.latestCommitSHA}` : ''
  const sevRank = { critical: 0, high: 1, medium: 2, low: 3 }
  const top = [...open].sort((a, b) => (sevRank[a.severity] ?? 9) - (sevRank[b.severity] ?? 9)).slice(0, 6)

  return <section className="feature-view repo-detail-view">
    <section className="charts-grid repo-detail-grid">
      <div className="panel repo-overview-panel">
        <div className="panel-head">
          <div><h2><ProviderIcon provider={repo.provider}/> Overview</h2><p className="feature-note">{href && <ExternalLink href={href} label={`Open ${repo.name} in ${forge}`}>Open in {forge}</ExternalLink>}</p></div>
          <span className={`pill ${repo.health === 'failing' || repo.status === 'Blocked' ? 'failed' : repo.health === 'never-scanned' ? 'canceled' : 'success'}`}>{repo.status === 'Blocked' ? 'Blocked' : repo.health === 'failing' ? 'Failing' : repo.health === 'never-scanned' ? 'Never scanned' : 'Healthy'}</span>
        </div>
        <div className="overview-rows">
          <div><small>Clone URL</small><code>{repo.cloneUrl}</code></div>
          <div><small>Default branch</small><code>{repo.defaultBranch}</code></div>
          <div><small>Last scanned commit</small>{repo.latestCommitSHA ? <ExternalLink href={commitUrl} label={`Open commit ${repo.latestCommitSHA.slice(0, 7)}`}><code>{repo.latestCommitSHA.slice(0, 10)}</code></ExternalLink> : <span>Not scanned yet</span>}</div>
          <div><small>Last successful scan</small><span>{lastScan ? `${pretty(lastScan.scanner)} · ${relativeTime(lastScan.finishedAt || lastScan.startedAt || lastScan.createdAt)}` : 'Never'}</span></div>
          <div><small>Open findings</small>{open.length ? <button className="text-button" onClick={() => openFindings({ repositoryId: repo.id })}>{open.length} →</button> : <span>0</span>}</div>
        </div>
        {repo.status === 'Blocked' && <div className="blocked-note" role="note"><span>Blocked by policy <b>Block verified findings</b>: a verified secret was found ({card.verified} open). Scheduled scans are paused until an admin unblocks the repository.</span>{canUnblock && onUnblock && <button className="secondary" onClick={() => onUnblock(repo)}>Unblock repository</button>}</div>}
        {canRun && <button className="primary" onClick={() => onRun(repo.id)}>Run scan</button>}
      </div>
      <div className="panel repo-scorecard-panel">
        <div className="panel-head"><h2>Security scorecard</h2></div>
        <div className="scorecard-grade"><strong className={card.tone}>{card.grade}</strong><span>{card.score}<small>/100</small></span></div>
        <div className="scorecard-rows">
          <div><span>Verified secrets</span><b className={card.verified ? 'critical' : ''}>{card.verified}</b></div>
          <div><span>Other secret findings</span><b>{card.others}</b></div>
          <div><span>Dependency updates pending</span><b>{card.updates}</b></div>
          <div><span>Vulnerabilities &amp; code issues</span><b>{card.issues}</b></div>
          <div><span>Recent scan failures</span><b>{card.failed} <small>/ last {card.recentCount}</small></b></div>
          <div><span>Success rate</span><b>{card.successRate === null ? '—' : `${card.successRate}%`}</b></div>
        </div>
      </div>
    </section>
    <div className="panel repo-findings-panel">
      <div className="panel-head">
        <div><h2>Open findings</h2><p className="feature-note">Most severe first. Open a finding to see the exact issue, the file on {forge}, and triage options.</p></div>
        {open.length > top.length && <button className="text-button" onClick={() => openFindings({ repositoryId: repo.id })}>View all {open.length} →</button>}
      </div>
      {top.length ? <ul className="repo-finding-list">{top.map(f => <li key={f.fingerprint}>
        <span className={`sev ${f.severity}`}>{pretty(f.severity)}</span>
        <button className="text-button" onClick={() => openFindings({ repositoryId: repo.id, fingerprint: f.fingerprint })}>{f.detector === 'RenovateDependency' ? f.detail : f.detector}</button>
        <span className="repo-finding-where">{f.fileUrl ? <ExternalLink href={f.fileUrl} label={`Open ${f.safePath} on ${forge}`}>{f.safePath}{f.scanner === 'trufflehog' ? (f.detail ? `:${f.detail.replace('line ', '')}` : '') : f.sourceLine ? `:${f.sourceLine}` : ''}</ExternalLink> : f.safePath}</span>
        {f.pullRequestUrl && <ExternalLink href={f.pullRequestUrl} label={`Open pull request #${f.pullRequestNumber}`}>PR #{f.pullRequestNumber}</ExternalLink>}
      </li>)}</ul> : <div className="empty-state"><span>No open findings. {repoRuns.length ? 'Nice.' : 'Run a scan to check this repository.'}</span></div>}
    </div>
    <ScanFlowPanel repo={repo} token={token} runs={repoRuns} onLogs={onLogs} relativeTime={relativeTime}/>
    <SbomPanel repositoryId={repo.id} token={token} canRun={canRun} relativeTime={relativeTime}/>
    <div className="panel repo-languages-panel">
      <div className="panel-head"><div><h2>Languages</h2><p className="feature-note">Byte share per language, from the provider's repository metadata.</p></div></div>
      <LanguageBar languages={languages}/>
    </div>
    <PaginatedRuns runs={repoRuns} repos={[repo]} canRun={canRun} showRunButton={false} onRun={() => onRun(repo.id)} onCancel={onCancel} onLogs={onLogs} pretty={pretty} relativeTime={relativeTime}/>
  </section>
}
