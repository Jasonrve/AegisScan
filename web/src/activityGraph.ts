import type { Node, Edge } from '@xyflow/react'
import type { Activity, ActivityRun, SbomSummary } from './dataClient'

export const IN_FLIGHT = ['queued', 'preparing', 'running']
/** A queued run whose NotBefore is in the future is a retry waiting out its backoff, not a stuck run. */
export const isRetryPending = (run: Pick<ActivityRun, 'status' | 'notBefore'>, now = Date.now()) => run.status === 'queued' && !!run.notBefore && new Date(run.notBefore).getTime() > now
export const statusLabel = (run?: Pick<ActivityRun, 'status' | 'notBefore' | 'attempt'>, now = Date.now()) => !run ? 'never run' : isRetryPending(run, now) ? `retry pending (attempt ${run.attempt})` : run.status.replace(/_/g, ' ')

export const SEVERITIES = ['critical', 'high', 'medium', 'low'] as const
export type Severity = typeof SEVERITIES[number]
export type SeverityCounts = Record<Severity, number>
const worstOf = (sev?: SeverityCounts) => SEVERITIES.find(s => (sev?.[s] ?? 0) > 0)

export type FlowNodeData = Record<string, unknown> & {
  kind: 'repo' | 'scanner' | 'findings' | 'artifact'
  title: string
  subtitle?: string
  provider?: string
  chip?: string
  status?: string
  runId?: string
  scannerId?: string
  category?: string
  at?: string
  when?: string
  attempt?: number
  /** Headline number of a result node (open findings / vulnerabilities) and what it counts. */
  count?: number
  caption?: string
  sev?: SeverityCounts
  worst?: Severity
  error?: string
}

const COL = { repo: 0, scanner: 340, output: 720 }
const ROW = 148

/** Repository → scanners → what each run produced. Edge labels carry the item counts. Columns and rows are laid out by hand (no layout library). */
export function buildScanFlow({ repo, activity, sboms, now = Date.now() }: { repo: { id: string; name: string; provider?: string; defaultBranch?: string; latestCommitSHA?: string }; activity: Activity; sboms: SbomSummary[]; now?: number }): { nodes: Node<FlowNodeData>[]; edges: Edge[]; neverRun: string[]; rows: number } {
  const nodes: Node<FlowNodeData>[] = []; const edges: Edge[] = []
  const scannerYs: number[] = []
  const edge = (id: string, source: string, target: string, label: string | undefined, inFlight: boolean): Edge => ({ id, source, target, label, animated: inFlight, type: 'smoothstep', className: inFlight ? 'flying' : undefined })
  let row = 0
  const ran = activity.scanners.filter(s => activity.latest.some(r => r.repositoryId === repo.id && r.scanner === s.id))
  // Scanners that never ran here would only shrink the graph: they are listed beside it instead (all of them show when nothing ran yet).
  const shown = ran.length ? ran : activity.scanners
  for (const scanner of shown) {
    const run = activity.latest.find(r => r.repositoryId === repo.id && r.scanner === scanner.id)
    const inFlight = !!run && IN_FLIGHT.includes(run.status)
    const open = activity.open.find(o => o.repositoryId === repo.id && o.scanner === scanner.id)?.count ?? 0
    const split = Object.fromEntries(SEVERITIES.map(s => [s, activity.severities?.filter(x => x.scanner === scanner.id && x.severity === s).reduce((n, x) => n + x.count, 0) ?? 0])) as SeverityCounts
    // Outputs: one node per SBOM artifact for the sbom scanner, otherwise one findings node.
    const outputs: { id: string; data: FlowNodeData; label: string }[] = scanner.id === 'sbom'
      ? sboms.map(s => {
        const sev: SeverityCounts = { critical: s.critical, high: s.high, medium: s.medium, low: s.low }
        return { id: `out:sbom:${s.id}`, label: s.hasDocument ? `${s.componentCount} packages` : 'failed', data: { kind: 'artifact', title: s.kind === 'repository' ? 'Repository files' : s.ref, chip: s.kind === 'repository' ? undefined : s.source === 'registry' ? 'from registry' : s.origin, count: s.critical + s.high + s.medium + s.low, caption: 'vulnerabilities', sev, worst: worstOf(sev), status: s.hasDocument ? 'succeeded' : 'failed', error: s.hasDocument ? undefined : s.error, scannerId: 'sbom' } }
      })
      : run ? [{ id: `out:${scanner.id}`, label: run.outputInterpreted ? `${run.findingCount} found` : 'not interpreted', data: { kind: 'findings', title: scanner.name, count: open, caption: run.outputInterpreted ? `open finding${open === 1 ? '' : 's'}` : 'output not interpreted', sev: split, worst: worstOf(split), status: 'succeeded', scannerId: scanner.id } }] : []
    const first = row
    outputs.forEach((o, i) => { nodes.push({ id: o.id, type: 'flow', position: { x: COL.output, y: (first + i) * ROW }, data: o.data }); edges.push(edge(`e:${scanner.id}:${o.id}`, `scanner:${scanner.id}`, o.id, o.label, inFlight)) })
    row += Math.max(outputs.length, 1)
    const y = ((first + row - 1) / 2) * ROW; scannerYs.push(y)
    nodes.push({ id: `scanner:${scanner.id}`, type: 'flow', position: { x: COL.scanner, y }, data: { kind: 'scanner', title: scanner.name, category: scanner.category, status: run ? (isRetryPending(run, now) ? 'queued' : run.status) : 'never', subtitle: statusLabel(run, now), runId: run?.id, at: run?.finishedAt || run?.startedAt || run?.createdAt, attempt: run?.attempt, scannerId: scanner.id } })
    // No label here: the scanner's own status pill says it, and the line moves while a run is in flight.
    edges.push(edge(`e:repo:${scanner.id}`, 'repo', `scanner:${scanner.id}`, undefined, inFlight))
  }
  const mid = scannerYs.length ? (Math.min(...scannerYs) + Math.max(...scannerYs)) / 2 : 0
  nodes.unshift({ id: 'repo', type: 'flow', position: { x: COL.repo, y: mid }, data: { kind: 'repo', title: repo.name, provider: repo.provider, subtitle: repo.defaultBranch, chip: repo.latestCommitSHA?.slice(0, 7) } })
  return { nodes, edges, neverRun: ran.length ? activity.scanners.filter(s => !ran.includes(s)).map(s => s.name) : [], rows: Math.max(row, 1) }
}
