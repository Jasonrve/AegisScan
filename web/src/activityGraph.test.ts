import { describe, expect, it } from 'vitest'
import { buildScanFlow, isRetryPending, statusLabel } from './activityGraph'
import type { Activity, ActivityRun, SbomSummary } from './dataClient'

const run = (over: Partial<ActivityRun>): ActivityRun => ({ id: 'r', repositoryId: 'a/b', scanner: 'trufflehog', mode: 'incremental', status: 'succeeded', attempt: 1, createdAt: '2026-01-01T00:00:00Z', findingCount: 0, outputInterpreted: true, commitSHA: '', ...over })
const activity = (latest: ActivityRun[], open: Activity['open'] = []): Activity => ({ scanners: [{ id: 'trufflehog', name: 'TruffleHog', category: 'secrets' }, { id: 'sbom', name: 'SBOM (Trivy)', category: 'vulnerabilities' }], latest, active: [], open, generatedAt: '' })
const sbom = (over: Partial<SbomSummary>): SbomSummary => ({ id: 's1', kind: 'image', ref: 'node:18', origin: 'detected', source: 'generated', commitSHA: '', generatedAt: '', componentCount: 478, critical: 7, high: 65, medium: 41, low: 0, error: '', hasDocument: true, ...over })
const repo = { id: 'a/b', name: 'a/b', defaultBranch: 'main', latestCommitSHA: 'abcdef123456' }

describe('buildScanFlow', () => {
  it('labels edges with item counts and animates only runs in flight', () => {
    const { nodes, edges } = buildScanFlow({ repo, activity: activity([run({ id: 't1', findingCount: 12 }), run({ id: 's1', scanner: 'sbom', status: 'running' })], [{ repositoryId: 'a/b', scanner: 'trufflehog', count: 3 }]), sboms: [sbom({}), sbom({ id: 's2', kind: 'repository', ref: '', componentCount: 380 })] })
    expect(edges.find(e => e.target === 'out:trufflehog')).toMatchObject({ label: '12 found', animated: false })
    expect(edges.find(e => e.target === 'scanner:sbom')).toMatchObject({ label: undefined, animated: true })
    expect(edges.filter(e => e.source === 'scanner:sbom').map(e => e.label)).toEqual(['478 packages', '380 packages'])
    expect(nodes.find(n => n.id === 'out:trufflehog')?.data).toMatchObject({ count: 3, caption: 'open findings' })
    expect(nodes.find(n => n.id === 'repo')?.data).toMatchObject({ subtitle: 'main', chip: 'abcdef1' })
    expect(nodes.find(n => n.id === 'out:sbom:s1')?.data).toMatchObject({ count: 113, worst: 'critical', sev: { critical: 7, high: 65 } })
    expect(edges.find(e => e.target === 'scanner:trufflehog')?.label).toBeUndefined() // idle link: no label
  })
  it('shows never-run scanners and uninterpreted output instead of zero', () => {
    const { nodes, edges, neverRun } = buildScanFlow({ repo, activity: activity([run({ scanner: 'trufflehog', outputInterpreted: false })]), sboms: [] })
    expect(edges.find(e => e.target === 'out:trufflehog')?.label).toBe('not interpreted')
    expect(nodes.some(n => n.id === 'scanner:sbom')).toBe(false) // never ran here: listed, not drawn
    expect(neverRun).toEqual(['SBOM (Trivy)'])
    expect(nodes.some(n => n.id.startsWith('out:sbom'))).toBe(false)
  })
})

describe('retry backoff', () => {
  const now = Date.parse('2026-01-01T00:00:00Z')
  it('treats a queued run with a future NotBefore as a pending retry', () => {
    expect(isRetryPending({ status: 'queued', notBefore: '2026-01-01T00:05:00Z' }, now)).toBe(true)
    expect(isRetryPending({ status: 'queued', notBefore: '2025-12-31T23:00:00Z' }, now)).toBe(false)
    expect(statusLabel({ status: 'queued', notBefore: '2026-01-01T00:05:00Z', attempt: 2 }, now)).toBe('retry pending (attempt 2)')
  })
})
