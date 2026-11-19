import { Fragment, useCallback, useEffect, useState } from 'react'
import { loadSboms, loadSbom, downloadSbom, queueScan, saveRepositoryImages, type SbomSummary, type SbomDetail, type SbomComponent } from './dataClient'
import { ExternalLink } from './PaginatedViews'

const SEVERITIES = ['critical', 'high', 'medium', 'low'] as const

function VulnCounts({ s }: { s: Pick<SbomSummary, 'critical' | 'high' | 'medium' | 'low'> }) {
  const shown = SEVERITIES.filter(k => s[k] > 0)
  return shown.length ? <span className="sbom-counts">{shown.map(k => <span key={k} className={`sev ${k}`} title={`${s[k]} ${k}`}>{s[k]} {k}</span>)}</span> : <span className="sbom-clean">none</span>
}

function ComponentRow({ c }: { c: SbomComponent }) {
  return <tr>
    <td title={c.purl}><code>{c.name}</code></td><td>{c.version}</td><td>{c.license || '—'}</td>
    <td>{c.vulnerabilities.length ? c.vulnerabilities.map(v => <span key={v.id} className="sbom-vuln" title={[v.summary, v.fix].filter(Boolean).join(' — ')}>
      <span className={`sev ${v.severity}`}>{v.severity}</span>{v.url ? <ExternalLink href={v.url} label={`Open advisory ${v.id}`}>{v.id}</ExternalLink> : v.id}</span>) : '—'}</td>
  </tr>
}

function SbomDetailView({ repositoryId, sbom, token }: { repositoryId: string; sbom: SbomSummary; token?: string }) {
  const [detail, setDetail] = useState<SbomDetail | null>(null)
  const [failed, setFailed] = useState(false)
  const [onlyVulnerable, setOnlyVulnerable] = useState(false)
  const [search, setSearch] = useState('')
  useEffect(() => { loadSbom(repositoryId, sbom.id, fetch, token).then(setDetail).catch(() => setFailed(true)) }, [repositoryId, sbom.id, sbom.generatedAt, token])
  if (failed) return <div className="empty-state"><span>Could not load this SBOM.</span></div>
  if (!detail) return <div className="empty-state"><span>Loading components…</span></div>
  const q = search.trim().toLowerCase()
  const groups = detail.groups.map(g => ({ ...g, rows: g.components.filter(c => (!onlyVulnerable || c.vulnerabilities.length) && (!q || c.name.toLowerCase().includes(q))) })).filter(g => g.rows.length)
  return <div className="sbom-detail">
    <div className="sbom-detail-tools">
      <input aria-label="Filter components" placeholder="Filter components" value={search} onChange={e => setSearch(e.target.value)}/>
      <label><input type="checkbox" checked={onlyVulnerable} onChange={e => setOnlyVulnerable(e.target.checked)}/> Vulnerable only</label>
    </div>
    {groups.length ? groups.map(g => <details key={g.name} open={groups.length === 1 || g.components.some(c => c.vulnerabilities.length > 0)}>
      <summary><b>{g.name}</b>{g.ecosystem && <span className="chip">{g.ecosystem}</span>} <small>{g.components.length} packages · {g.components.filter(c => c.vulnerabilities.length).length} vulnerable</small></summary>
      <table className="sbom-table"><thead><tr><th>Package</th><th>Version</th><th>License</th><th>Vulnerabilities</th></tr></thead><tbody>{g.rows.map(c => <Fragment key={c.purl || c.name + c.version}>{ComponentRow({ c })}</Fragment>)}</tbody></table>
    </details>) : <div className="empty-state"><span>{detail.hasDocument ? 'No components match.' : 'No SBOM stored yet.'}</span></div>}
  </div>
}

/** Repository view: the checkout and every linked container image as an artifact with its SBOM, vulnerabilities and (on demand) its dependency list. */
export default function SbomPanel({ repositoryId, token, canRun, relativeTime }: { repositoryId: string; token?: string; canRun: boolean; relativeTime: (value?: string) => string }) {
  const [data, setData] = useState<{ images: string[]; artifacts: SbomSummary[] } | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [images, setImages] = useState('')
  const [message, setMessage] = useState('')
  const reload = useCallback(() => loadSboms(repositoryId, fetch, token).then(d => { setData(d); setImages(d.images.join('\n')) }).catch(() => setData({ images: [], artifacts: [] })), [repositoryId, token])
  useEffect(() => { setData(null); setOpen(null); reload() }, [reload])
  const run = (action: () => Promise<unknown>, ok: string) => action().then(() => setMessage(ok)).catch(() => setMessage('That did not work: check the image references (one per line, at most 25) and your permissions.'))

  return <div className="panel repo-sbom-panel">
    <div className="panel-head">
      <div><h2>Artifacts &amp; SBOMs</h2><p className="feature-note">The repository's dependencies and every container image linked to it, from CycloneDX SBOMs (read from the image's registry when published there, otherwise generated). Images are found in Dockerfiles and manifests, or added below.</p></div>
      <div className="sbom-actions"><button className="secondary" onClick={() => { setMessage(''); reload() }}>Refresh</button>
        {canRun && <button className="primary" onClick={() => run(() => queueScan({ repositoryId, scanner: 'sbom', mode: 'incremental', token }), 'SBOM scan queued; refresh in a minute.')}>Scan SBOMs</button>}</div>
    </div>
    {message && <p className="feature-note" role="status">{message}</p>}
    {data === null ? <div className="empty-state"><span>Loading…</span></div> : data.artifacts.length === 0 ? <div className="empty-state"><span>No SBOMs yet. {canRun ? 'Run “Scan SBOMs” to build them.' : 'An operator can run an SBOM scan.'}</span></div> :
      <table className="sbom-table sbom-artifacts"><thead><tr><th>Artifact</th><th>Origin</th><th>Packages</th><th>Vulnerabilities</th><th>SBOM</th><th>Updated</th><th/></tr></thead><tbody>
        {data.artifacts.map(a => <Fragment key={a.id}>
          <tr>
            <td><code>{a.kind === 'repository' ? 'Repository files' : a.ref}</code>{a.error && <div className="sbom-error" role="alert">Last refresh failed: {a.error}</div>}</td>
            <td>{a.kind === 'repository' ? '—' : a.origin}</td><td>{a.hasDocument ? a.componentCount : '—'}</td>
            <td>{a.hasDocument ? <VulnCounts s={a}/> : '—'}</td><td>{a.hasDocument ? (a.source === 'registry' ? 'from registry' : 'generated') : '—'}</td>
            <td>{a.hasDocument ? relativeTime(a.generatedAt) : '—'}</td>
            <td className="sbom-row-actions">{a.hasDocument && <button className="text-button" aria-expanded={open === a.id} onClick={() => setOpen(open === a.id ? null : a.id)}>{open === a.id ? 'Hide' : 'Details'}</button>}
              {a.hasDocument && <button className="text-button" onClick={() => run(() => downloadSbom(repositoryId, a, token || ''), '')}>Download</button>}</td>
          </tr>
          {open === a.id && <tr><td colSpan={7}><SbomDetailView repositoryId={repositoryId} sbom={a} token={token}/></td></tr>}
        </Fragment>)}
      </tbody></table>}
    {canRun && data && <div className="sbom-images">
      <label htmlFor="sbom-images">Linked container images <small>(one per line, e.g. 123456789012.dkr.ecr.eu-west-1.amazonaws.com/team/app:1.2.3)</small></label>
      <textarea id="sbom-images" rows={3} value={images} onChange={e => setImages(e.target.value)}/>
      <button className="secondary" onClick={() => run(() => saveRepositoryImages(repositoryId, images.split('\n').map(i => i.trim()).filter(Boolean), token || ''), 'Images saved; they are included in the next SBOM scan.')}>Save images</button>
    </div>}
  </div>
}
