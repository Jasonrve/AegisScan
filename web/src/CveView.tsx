import { useContext, useEffect, useState } from 'react'
import { loadCve, loadCves, type CveDetail, type CveList } from './dataClient'
import { ExternalLink, NavigationContext } from './PaginatedViews'

const VECTOR_LABELS: Record<string, [string, Record<string, string>]> = {
  AV: ['Attack vector', { N: 'Network', A: 'Adjacent', L: 'Local', P: 'Physical' }],
  AC: ['Attack complexity', { L: 'Low', H: 'High' }],
  PR: ['Privileges required', { N: 'None', L: 'Low', H: 'High' }],
  UI: ['User interaction', { N: 'None', R: 'Required' }],
  S: ['Scope', { U: 'Unchanged', C: 'Changed' }],
  C: ['Confidentiality impact', { N: 'None', L: 'Low', H: 'High' }],
  I: ['Integrity impact', { N: 'None', L: 'Low', H: 'High' }],
  A: ['Availability impact', { N: 'None', L: 'Low', H: 'High' }],
}
/** "CVSS:3.1/AV:N/AC:L/..." -> readable metric/value pairs (CVSS 3.x only; anything else is shown raw). */
function decodeVector(vector: string) {
  return vector.split('/').map(part => part.split(':')).filter(([k]) => VECTOR_LABELS[k]).map(([k, v]) => ({ label: VECTOR_LABELS[k][0], value: VECTOR_LABELS[k][1][v] || v }))
}
const scoreTone = (score: number | null, severity: string) => score === null ? severity : score >= 9 ? 'critical' : score >= 7 ? 'high' : score >= 4 ? 'medium' : 'low'
const Score = ({ score, severity, big }: { score: number | null; severity: string; big?: boolean }) =>
  <span className={`cve-score ${scoreTone(score, severity)}${big ? ' big' : ''}`} title="CVSS base score (highest across rating sources)">{score === null ? 'n/a' : score.toFixed(1)}</span>
const date = (v?: string | null) => v ? new Date(v).toLocaleDateString() : '—'

function CveDetailView({ id, days, token, onBack, relativeTime }: { id: string; days: number; token: string; onBack: () => void; relativeTime: (value?: string) => string }) {
  const { navigate, openFindings } = useContext(NavigationContext)
  const [cve, setCve] = useState<CveDetail | null>(null)
  const [error, setError] = useState(false)
  useEffect(() => { setCve(null); setError(false); loadCve(id, token, days).then(setCve).catch(() => setError(true)) }, [id, token, days])
  const back = <button className="text-button" onClick={onBack}>← All CVEs</button>
  if (error) return <section className="feature-view">{back}<div className="empty-state"><span>Could not load {id} (it may no longer be open on any repository).</span></div></section>
  if (!cve) return <section className="feature-view">{back}<div className="empty-state"><span>Loading {id}…</span></div></section>
  const decoded = decodeVector(cve.vector)
  return <section className="feature-view cve-detail">
    {back}
    <div className="panel cve-head">
      <Score score={cve.score} severity={cve.severity} big/>
      <div>
        <h2>{cve.id} <span className={`sev ${cve.severity}`}>{cve.severity}</span>{cve.isNew && <span className="fn-chip cve-new">new</span>}</h2>
        <p className="feature-note"><ExternalLink href={cve.advisoryUrl} label={`Open advisory ${cve.id}`}>Advisory</ExternalLink> · {cve.repos.length} affected repositor{cve.repos.length === 1 ? 'y' : 'ies'} · {cve.occurrences} occurrence{cve.occurrences === 1 ? '' : 's'} · {cve.fixAvailable ? 'fix available' : 'no fix yet'}</p>
      </div>
    </div>
    <div className="charts-grid">
      <div className="panel">
        <div className="panel-head"><h2>Description</h2></div>
        <p className="cve-description">{cve.description || 'No description recorded: only the SBOM scanner provides one, and this CVE was reported by another scanner.'}</p>
        <div className="overview-rows">
          <div><small>First seen here</small><span>{cve.firstSeen ? `${date(cve.firstSeen)} (${relativeTime(cve.firstSeen)})` : '—'}</span></div>
          <div><small>Published</small><span>{date(cve.published)}</span></div>
          <div><small>Last modified</small><span>{date(cve.updated)}</span></div>
          <div><small>Weakness (CWE)</small>{cve.cwes.length ? <span>{cve.cwes.map(c => <ExternalLink key={c} href={`https://cwe.mitre.org/data/definitions/${c}.html`} label={`CWE-${c}`}>CWE-{c} </ExternalLink>)}</span> : <span>—</span>}</div>
        </div>
      </div>
      <div className="panel">
        <div className="panel-head"><h2>Scoring</h2></div>
        {cve.score === null ? <div className="empty-state"><span>No CVSS score available.</span></div> : <>
          <div className="cve-vector"><b>{cve.method || 'CVSS'}</b> <code>{cve.vector}</code></div>
          {decoded.length > 0 && <div className="cve-metrics">{decoded.map(d => <div key={d.label}><small>{d.label}</small><b>{d.value}</b></div>)}</div>}
        </>}
        {cve.ratings.length > 0 && <table className="cve-ratings"><thead><tr><th>Source</th><th>Score</th><th>Severity</th><th>Method</th></tr></thead>
          <tbody>{cve.ratings.map((r, i) => <tr key={i}><td>{r.source || '—'}</td><td>{r.score === null ? '—' : r.score.toFixed(1)}</td><td><span className={`sev ${r.severity}`}>{r.severity || '—'}</span></td><td>{r.method || '—'}</td></tr>)}</tbody></table>}
      </div>
    </div>
    {cve.references.length > 0 && <div className="panel"><div className="panel-head"><h2>References</h2></div><ul className="cve-refs">{cve.references.map(u => <li key={u}><ExternalLink href={u} label={u}>{u}</ExternalLink></li>)}</ul></div>}
    <div className="panel">
      <div className="panel-head"><div><h2>Affected repositories ({cve.repos.length})</h2><p className="feature-note">Open findings of this CVE, with the package and version it was found in.</p></div></div>
      {cve.repos.length === 0 ? <div className="empty-state"><span>No repository currently has this CVE open.</span></div> : cve.repos.map(r => <div className="cve-repo" key={r.id}>
        <div className="cve-repo-head">
          <button className="text-button" onClick={() => navigate(r.id)}><b>{r.name}</b></button>
          {r.status === 'Blocked' && <span className="pill failed">Blocked</span>}
          <button className="text-button" onClick={() => openFindings({ repositoryId: r.id })}>Findings →</button>
        </div>
        <table className="cve-ratings"><thead><tr><th>Package</th><th>Version</th><th>Fix</th><th>Where</th><th>Assignee</th><th>First seen</th></tr></thead>
          <tbody>{r.findings.map(f => <tr key={f.fingerprint}><td><code>{f.package}</code></td><td>{f.version || '—'}</td><td>{f.fix || '—'}</td><td>{f.path || '—'}</td><td>{f.assignee || '—'}</td><td>{date(f.firstSeen)}</td></tr>)}</tbody></table>
      </div>)}
    </div>
  </section>
}

/** Open CVEs, newest and most severe first; click one for its description, scoring and every affected repository. */
export default function CveView({ token, relativeTime }: { token: string; relativeTime: (value?: string) => string }) {
  const [days, setDays] = useState(7)
  const [severity, setSeverity] = useState('')
  const [search, setSearch] = useState('')
  const [newOnly, setNewOnly] = useState(false)
  const [data, setData] = useState<CveList | null>(null)
  const [failed, setFailed] = useState(false)
  const [selected, setSelected] = useState('')
  useEffect(() => {
    let cancelled = false
    const t = setTimeout(() => loadCves(token, { days, severity, search, newOnly }).then(d => { if (!cancelled) { setData(d); setFailed(false) } }).catch(() => { if (!cancelled) setFailed(true) }), 250)
    return () => { cancelled = true; clearTimeout(t) }
  }, [token, days, severity, search, newOnly])
  if (selected) return <CveDetailView id={selected} days={days} token={token} onBack={() => setSelected('')} relativeTime={relativeTime}/>
  return <section className="feature-view cve-view">
    <div className="panel">
      <div className="panel-head"><div><h2>CVEs</h2><p className="feature-note">Vulnerabilities currently open on your repositories. “New” = first found after a repository's baseline scan, within the chosen window.</p></div>
        {data && <span><b>{data.newCount}</b> new · <b>{data.total}</b> shown</span>}</div>
      <div className="cve-filters">
        <input aria-label="Search CVEs" placeholder="Search CVE id or package" value={search} onChange={e => setSearch(e.target.value)}/>
        <select aria-label="Severity" value={severity} onChange={e => setSeverity(e.target.value)}><option value="">All severities</option>{['critical', 'high', 'medium', 'low'].map(s => <option key={s} value={s}>{s}</option>)}</select>
        <select aria-label="New within" value={days} onChange={e => setDays(Number(e.target.value))}>{[1, 7, 30, 90].map(d => <option key={d} value={d}>New within {d} day{d === 1 ? '' : 's'}</option>)}</select>
        <label><input type="checkbox" checked={newOnly} onChange={e => setNewOnly(e.target.checked)}/> New only</label>
      </div>
      {failed ? <div className="empty-state"><span>Could not load CVEs.</span></div> : !data ? <div className="empty-state"><span>Loading CVEs…</span></div> : data.items.length === 0 ? <div className="empty-state"><span>No CVEs match.</span></div> :
        <ul className="cve-list">{data.items.map(c => <li key={c.id}><button className="cve-row" onClick={() => setSelected(c.id)}>
          <Score score={c.score} severity={c.severity}/>
          <div className="cve-main"><b>{c.id}</b>{c.isNew && <span className="fn-chip cve-new">new</span>}<small>{c.packages.join(', ')}{c.packageCount > c.packages.length ? ` +${c.packageCount - c.packages.length}` : ''}</small>{c.summary && <small className="cve-summary">{c.summary}</small>}</div>
          <span className={`sev ${c.severity}`}>{c.severity}</span>
          <span className="cve-repos">{c.repos} repo{c.repos === 1 ? '' : 's'}</span>
          {c.fixAvailable ? <span className="fn-chip fix-chip">fix available</span> : <span className="fn-chip">no fix yet</span>}
          <small>{relativeTime(c.firstSeen)}</small>
        </button></li>)}</ul>}
    </div>
  </section>
}
