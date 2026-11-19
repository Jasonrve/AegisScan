import { useContext, useEffect, useMemo, useState } from 'react'
import { ReactFlow, Handle, Position, type NodeProps, type Node } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { loadSboms, type Execution, type SbomSummary } from './dataClient'
import { buildScanFlow, SEVERITIES, type FlowNodeData, type SeverityCounts } from './activityGraph'
import { useActivity } from './useActivity'
import { NavigationContext, ProviderIcon } from './PaginatedViews'

const ICONS: Record<string, string> = {
  secrets: 'M11 5.5a3 3 0 1 1-2.1 5.1L6 13.5H4.5V12H3v-1.5l3.9-3.9A3 3 0 0 1 11 5.5Z',
  dependencies: 'M9 2 15 5v8l-6 3-6-3V5l6-3Zm0 0v7m6-4-6 4-6-4',
  vulnerabilities: 'M9 2.5 15 5v4c0 4-2.5 6.5-6 8-3.5-1.5-6-4-6-8V5l6-2.5Z',
  code: 'm6 5-4 4 4 4m6-8 4 4-4 4',
  iac: 'M9 3 16 7l-7 4-7-4 7-4Zm-7 7 7 4 7-4',
  other: 'M9 3a6 6 0 1 0 0 12A6 6 0 0 0 9 3Z',
}
const CategoryIcon = ({ category }: { category?: string }) => <svg width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={ICONS[category || 'other'] || ICONS.other}/></svg>

/** Thin stacked bar of severity counts, with the non-zero counts spelled out beneath it. */
function SeverityBar({ sev }: { sev: SeverityCounts }) {
  const total = SEVERITIES.reduce((n, s) => n + sev[s], 0)
  if (!total) return <div className="fn-clean">no issues</div>
  return <>
    <div className="fn-bar" aria-hidden="true">{SEVERITIES.filter(s => sev[s]).map(s => <i key={s} className={s} style={{ flexGrow: sev[s] }}/>)}</div>
    <div className="fn-legend">{SEVERITIES.filter(s => sev[s]).map(s => <span key={s}><i className={s}/>{sev[s]} {s}</span>)}</div>
  </>
}

function FlowNode({ data }: NodeProps<Node<FlowNodeData>>) {
  const handles = <><Handle type="target" position={Position.Left} isConnectable={false}/><Handle type="source" position={Position.Right} isConnectable={false}/></>
  if (data.kind === 'repo') return <div className="fn fn-repo">{handles}
    <div className="fn-title"><ProviderIcon provider={data.provider || 'github'}/><b>{data.title}</b></div>
    <div className="fn-chips">{data.subtitle && <code>{data.subtitle}</code>}{data.chip && <code>{data.chip}</code>}</div>
  </div>
  if (data.kind === 'scanner') return <div className={`fn fn-scanner ${data.status}`}>{handles}
    <div className="fn-title"><span className="fn-icon"><CategoryIcon category={data.category}/></span><b>{data.title}</b></div>
    <div className="fn-row"><span className={`pill ${data.status === 'never' ? 'canceled' : data.status}`}>{data.subtitle}</span><small>{[data.when, data.attempt && data.attempt > 1 ? `attempt ${data.attempt}` : ''].filter(Boolean).join(' · ')}</small></div>
  </div>
  return <div className={`fn fn-result ${data.status === 'failed' ? 'bad' : `worst-${data.worst || 'none'}`}`}>{handles}
    {data.kind === 'artifact' && <div className="fn-title"><code>{data.title}</code>{data.chip && <span className="fn-chip">{data.chip}</span>}</div>}
    {data.status === 'failed' ? <div className="fn-error" title={data.error}>{data.error || 'SBOM failed'}</div> : <>
      <div className="fn-big"><strong>{data.count}</strong><span>{data.caption}</span></div>
      {data.sev && <SeverityBar sev={data.sev}/>}
    </>}
  </div>
}
const nodeTypes = { flow: FlowNode }

/** Repository → scanners → outputs. Edge pills say how many items each run picked up; moving lines are runs in flight. A static diagram: it never pans or zooms, so the page scrolls over it. */
export default function ScanFlowPanel({ repo, token, runs, onLogs, relativeTime }: { repo: { id: string; name: string; provider?: string; defaultBranch?: string; latestCommitSHA?: string }; token?: string; runs: Execution[]; onLogs: (run: Execution) => void; relativeTime: (value?: string) => string }) {
  const { openFindings, openRun } = useContext(NavigationContext)
  const { activity, failed } = useActivity(token, repo.id)
  const [sboms, setSboms] = useState<SbomSummary[]>([])
  const sbomRun = activity?.latest.find(r => r.scanner === 'sbom')
  useEffect(() => { loadSboms(repo.id, fetch, token).then(d => setSboms(d.artifacts)).catch(() => setSboms([])) }, [repo.id, token, sbomRun?.id, sbomRun?.status])
  const graph = useMemo(() => {
    if (!activity) return null
    const g = buildScanFlow({ repo, activity, sboms })
    return { ...g, nodes: g.nodes.map(n => n.data.at ? { ...n, data: { ...n.data, when: relativeTime(n.data.at) } } : n) }
  }, [activity, sboms, repo.id, repo.name, repo.provider, repo.defaultBranch, repo.latestCommitSHA])
  const click = (_: unknown, node: Node<FlowNodeData>) => {
    const { kind, runId } = node.data
    if (kind === 'scanner' && runId) { const run = runs.find(r => r.id === runId); run ? onLogs(run) : openRun(runId) }
    else if (kind === 'findings') openFindings({ repositoryId: repo.id })
    else if (kind === 'artifact') document.querySelector('.repo-sbom-panel')?.scrollIntoView({ behavior: 'smooth' })
  }
  return <div className="panel repo-flow-panel">
    <div className="panel-head"><div><h2>Scan flow</h2><p className="feature-note">The latest run of each scanner on this repository and what it produced. Click a scanner for its log, a result to open it.</p></div></div>
    {!graph ? <div className="empty-state"><span>{failed ? 'Could not load scan activity.' : 'Loading scan activity…'}</span></div> : <>
      <div className="flow-canvas" style={{ height: graph.rows * 148 + 24 }}>
        <ReactFlow nodes={graph.nodes} edges={graph.edges} nodeTypes={nodeTypes} fitView fitViewOptions={{ padding: 0.04 }} minZoom={0.4} maxZoom={1.15}
          panOnDrag={false} zoomOnScroll={false} zoomOnPinch={false} zoomOnDoubleClick={false} preventScrolling={false}
          nodesDraggable={false} nodesConnectable={false} onNodeClick={click} proOptions={{ hideAttribution: true }}
          defaultEdgeOptions={{ labelShowBg: true, labelBgPadding: [8, 4], labelBgBorderRadius: 10, labelBgStyle: { fill: 'var(--flow-pill-bg)', stroke: 'var(--flow-edge)' }, labelStyle: { fill: 'var(--flow-pill-fg)', fontSize: 11, fontWeight: 600 } }}/>
      </div>
      <div className="flow-legend"><span>Moving line = run in flight</span>{SEVERITIES.map(s => <span key={s}><i className={s}/>{s}</span>)}{graph.neverRun.length > 0 && <span className="flow-never">Not run here: {graph.neverRun.join(', ')}</span>}</div>
    </>}
  </div>
}
