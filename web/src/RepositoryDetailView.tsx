import { useEffect, useState } from 'react'
import { loadRepositoryLanguages, type Repository, type Execution, type Finding } from './dataClient'
import { repoHref, ProviderIcon, PaginatedRuns } from './PaginatedViews'

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
  if (!total) return <div className="empty-state"><span>No language data available for this repository yet.</span></div>
  const sorted = Object.entries(languages).sort((a, b) => b[1] - a[1])
  return <div className="language-breakdown">
    <div className="language-bar">{sorted.map(([name, bytes]) => <span key={name} style={{ width: `${(bytes / total) * 100}%`, background: languageColor(name) }} title={`${name} · ${((bytes / total) * 100).toFixed(1)}%`}/>)}</div>
    <div className="language-legend">{sorted.map(([name, bytes]) => <span key={name}><i style={{ background: languageColor(name) }}/>{name}<b>{((bytes / total) * 100).toFixed(1)}%</b></span>)}</div>
  </div>
}

/** Derived, client-side security posture score — no backend field backs this. Verified findings and blocked status dominate; recent failures nudge it down. */
function computeScorecard(repo: Repository, repoFindings: Finding[], repoRuns: Execution[]) {
  const verified = repoFindings.filter(f => f.verified).length
  const openOnly = repoFindings.length - verified
  const recent = repoRuns.slice(0, 10)
  const failed = recent.filter(r => ['failed', 'timed_out'].includes(r.status)).length
  let score = 100 - verified * 25 - openOnly * 8 - failed * 5
  if (repo.status === 'Blocked') score = Math.min(score, 40)
  score = Math.max(0, Math.min(100, score))
  const grade = score >= 90 ? 'A' : score >= 75 ? 'B' : score >= 60 ? 'C' : score >= 40 ? 'D' : 'F'
  const tone = score >= 75 ? 'green' : score >= 40 ? 'amber' : 'red'
  const successRate = recent.length ? Math.round((recent.filter(r => r.status === 'succeeded').length / recent.length) * 100) : null
  return { score, grade, tone, verified, openOnly, successRate, failed, recentCount: recent.length }
}

export default function RepositoryDetailView({ repo, runs, findings, token, canRun, onRun, onCancel, onLogs, onNavigate, pretty, relativeTime }: {
  repo: Repository
  runs: Execution[]
  findings: Finding[]
  token?: string
  canRun: boolean
  onRun: () => void
  onCancel: (id: string) => Promise<void>
  onLogs: (run: Execution) => void
  onNavigate: (view: string) => void
  pretty: (value: any) => string
  relativeTime: (value?: string) => string
}) {
  const [languages, setLanguages] = useState<Record<string, number> | null>(null)
  useEffect(() => {
    let cancelled = false
    setLanguages(null)
    loadRepositoryLanguages(repo.id, fetch, token).then(result => { if (!cancelled) setLanguages(result) }).catch(() => { if (!cancelled) setLanguages({}) })
    return () => { cancelled = true }
  }, [repo.id, token])

  const repoRuns = runs.filter(r => r.repositoryId === repo.id)
  const repoFindings = findings.filter(f => f.repositoryId === repo.id)
  const card = computeScorecard(repo, repoFindings, repoRuns)
  const href = repoHref(repo)
  const commitHref = href && repo.latestCommitSHA ? `${href}/commit/${repo.latestCommitSHA}` : ''

  return <section className="feature-view repo-detail-view">
    <button className="text-button back-link" onClick={() => onNavigate('Repositories')}>← Back to repositories</button>
    <section className="charts-grid repo-detail-grid">
      <div className="panel repo-overview-panel">
        <div className="panel-head">
          <div><h2><ProviderIcon provider={repo.provider}/> {repo.name}</h2><p className="feature-note">Repository overview</p></div>
          <span className={`pill ${repo.status === 'Ready' ? 'success' : 'failed'}`}>{repo.status}</span>
        </div>
        <div className="overview-rows">
          <div><small>Clone URL</small>{href ? <a href={href} target="_blank" rel="noreferrer">{repo.cloneUrl}</a> : <code>{repo.cloneUrl}</code>}</div>
          <div><small>Default branch</small><code>{repo.defaultBranch}</code></div>
          <div><small>Cache state</small><span className="status-good">{repo.cacheState}</span></div>
          <div><small>Latest commit</small>{commitHref ? <a href={commitHref} target="_blank" rel="noreferrer"><code>{repo.latestCommitSHA.slice(0, 10) || 'Unknown'}</code></a> : <code>{repo.latestCommitSHA || 'Unknown'}</code>}</div>
          <div><small>Active findings</small><span className={repo.activeFindingCount ? 'critical' : ''}>{repo.activeFindingCount}</span></div>
        </div>
        {canRun && <button className="primary" onClick={onRun}>Run scan</button>}
      </div>
      <div className="panel repo-scorecard-panel">
        <div className="panel-head"><h2>Security scorecard</h2></div>
        <div className="scorecard-grade"><strong className={card.tone}>{card.grade}</strong><span>{card.score}<small>/100</small></span></div>
        <div className="scorecard-rows">
          <div><span>Verified findings</span><b className={card.verified ? 'critical' : ''}>{card.verified}</b></div>
          <div><span>Other open findings</span><b>{card.openOnly}</b></div>
          <div><span>Recent scan failures</span><b>{card.failed} <small>/ last {card.recentCount}</small></b></div>
          <div><span>Success rate</span><b>{card.successRate === null ? '—' : `${card.successRate}%`}</b></div>
        </div>
      </div>
    </section>
    <div className="panel repo-languages-panel">
      <div className="panel-head"><div><h2>Languages</h2><p className="feature-note">Byte share per language, from the provider's repository metadata.</p></div></div>
      <LanguageBar languages={languages}/>
    </div>
    <PaginatedRuns runs={repoRuns} repos={[repo]} canRun={canRun} onRun={onRun} onCancel={onCancel} onLogs={onLogs} pretty={pretty} relativeTime={relativeTime}/>
  </section>
}
