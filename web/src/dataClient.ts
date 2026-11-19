export type Repository = {
  id: string
  name: string
  provider: string
  cloneUrl: string
  defaultBranch: string
  status: string
  cacheState: string
  activeFindingCount: number
  latestCommitSHA: string
  webUrl?: string
  health?: string
}

export type Execution = {
  id: string
  repositoryId: string
  scanner: string
  mode?: string
  outputInterpreted?: boolean
  status: string
  attempt: number
  createdAt?: string
  startedAt?: string
  finishedAt?: string
  findingCount: number
  commitSHA?: string
  commitUrl?: string
  repositoryUrl?: string
  failureReason?: string
  errorCode: string
  summary: Record<string, unknown>
  sanitizedLog: string
  links: { kind: string; title: string; url: string }[]
}

export type Finding = {
  fingerprint: string
  repositoryId: string
  detector: string
  verified: boolean
  safePath: string
  commitSHA: string
  firstSeen?: string
  lastSeen?: string
  state: string
  scanner: string
  severity: string
  detail: string
  executionId?: string
  category?: string
  helpUrl?: string
  preview?: string
  fileUrl?: string
  commitUrl?: string
  pullRequestUrl?: string
  pullRequestNumber?: number
  changelogUrl?: string
  advisories?: { id: string; summary: string; url: string }[]
  sourceLine?: number
  introducedCommit?: string
  introducedCommitUrl?: string
  assignee?: string
  dueAt?: string
  overdue?: boolean
}
export type AlertAck = { key: string; actor: string; acknowledgedAt: string; snoozeUntil?: string | null }
export type FindingStats = { open: number; overdue: number; mttrDays: number | null; resolvedLast90Days: number }
export type PolicyItem = { name: string; scanners: string[]; schedule?: string | null; fullScanSchedule?: string | null; incrementalByDefault: boolean; timeoutSeconds: number; retries: number; maxConcurrency: number; phase: string; message: string; repositories: number; createdAt?: string }
export type FindingHistoryItem = { timestamp: string; actor: string; from: string; to: string; reason: string }

export type SystemInfo = { version: string; kubernetes: { reachable: boolean; version?: string | null; namespace: string }; queue: { queued: number; running: number } }
export type RoleMapping = { idpGroup: string; aegisRole: string }

export type ScannerDefinition = {
  id: string
  name: string
  image: string
  command: string[]
  args: string[]
  builtIn: boolean
  enabled: boolean
  outputFormat: string
  category: string
  successExitCodes: number[]
}

import { userManager } from './auth'

type Fetcher = (input: string, options?: Record<string, any>) => Promise<any>

const baseUrl = () => import.meta.env?.VITE_API_BASE_URL || ''

async function request(path: string, options: Record<string, any> = {}, fetcher: Fetcher = fetch as unknown as Fetcher) {
  const response = await fetcher(`${baseUrl()}${path}`, { headers: { Accept: 'application/json', ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.token ? { Authorization: `Bearer ${options.token}` } : {}) }, ...options })
  if (response.status === 401) {
    userManager.signinRedirect({ state: window.location.pathname + window.location.search })
    throw new Error(`${path} request failed: 401`)
  }
  if (!response.ok) throw new Error(`${path} request failed: ${response.status}`)
  return response.status === 204 ? null : response.json()
}
// Repository ids may contain '/', which cannot travel in a path segment (the server reads '~' as '/').
const repoId = (id: string) => encodeURIComponent(id).replace(/%2F/gi, '~')
const PAGE = 200; const MAX_ROWS = 10000
/** Loads every page of a list endpoint (up to MAX_ROWS). `total` is the server's count, so views can say when the list is truncated. */
async function loadAllPages<T>(path: string, token: string, fetcher: Fetcher): Promise<T[] & { total?: number }> {
  const first = await request(`${path}?pageSize=${PAGE}&page=1`, { token }, fetcher)
  const rows: T[] = [...(Array.isArray(first) ? first : first?.items || first?.Items || [])]
  const total: number | undefined = Array.isArray(first) ? undefined : first?.total
  const pages = total ? Math.min(Math.ceil(total / PAGE), Math.ceil(MAX_ROWS / PAGE)) : 1
  const rest = await Promise.all(Array.from({ length: Math.max(0, pages - 1) }, (_, i) => request(`${path}?pageSize=${PAGE}&page=${i + 2}`, { token }, fetcher)))
  rest.forEach(page => rows.push(...(page?.items || page?.Items || [])))
  return Object.assign(rows, { total })
}
const items = <T,>(payload: any): T[] => Array.isArray(payload) ? payload : (payload?.items || payload?.Items || [])

export function normalizeRepository(repository: any): Repository {
  return { id: repository.id || repository.ID, name: repository.name || repository.Name, provider: repository.provider || repository.Provider || 'github', cloneUrl: repository.cloneUrl || repository.CloneURL || '', defaultBranch: repository.defaultBranch || repository.DefaultBranch || 'main', status: repository.status || repository.Status || 'Ready', cacheState: repository.cacheState || repository.CacheState || 'Unknown', activeFindingCount: Number(repository.activeFindingCount ?? repository.ActiveFindingCount ?? 0), latestCommitSHA: repository.latestCommitSHA || repository.LatestCommitSHA || '', webUrl: repository.webUrl || undefined, health: repository.health || 'healthy' }
}
export function normalizeExecution(execution: any): Execution {
  return { id: execution.id || execution.ID, repositoryId: execution.repositoryId || execution.RepositoryID, scanner: execution.scanner || execution.Scanner, mode: execution.mode || execution.Mode, status: execution.status || execution.Status, attempt: execution.attempt || execution.Attempt || 1, createdAt: execution.createdAt, startedAt: execution.startedAt || execution.StartedAt, findingCount: Number(execution.findingCount ?? 0), outputInterpreted: execution.outputInterpreted !== false, commitSHA: execution.commitSHA || '', commitUrl: execution.commitUrl || undefined, repositoryUrl: execution.repositoryUrl || undefined, failureReason: execution.failureReason || undefined, finishedAt: execution.finishedAt || execution.FinishedAt, errorCode: execution.errorCode || execution.ErrorCode || '', summary: execution.summary || execution.Summary || {}, sanitizedLog: execution.sanitizedLog || execution.SanitizedLog || '', links: execution.links || execution.Links || [] }
}
export function normalizeFinding(finding: any): Finding {
  return { fingerprint: finding.fingerprint || finding.Fingerprint, repositoryId: finding.repositoryId || finding.RepositoryID, detector: finding.detector || finding.Detector || 'Unknown detector', verified: Boolean(finding.verified ?? finding.Verified), safePath: finding.safePath || finding.SafePath || '', commitSHA: finding.commitSHA || finding.CommitSHA || '', firstSeen: finding.firstSeen || finding.FirstSeen, lastSeen: finding.lastSeen || finding.LastSeen, state: finding.state || finding.State || 'open', scanner: finding.scanner || '', category: finding.category || '', helpUrl: finding.helpUrl || undefined, severity: finding.severity || (finding.verified ?? finding.Verified ? 'critical' : 'medium'), detail: finding.detail || '', executionId: finding.executionId, preview: finding.preview || '', fileUrl: finding.fileUrl || undefined, commitUrl: finding.commitUrl || undefined, pullRequestUrl: finding.pullRequestUrl || undefined, pullRequestNumber: finding.pullRequestNumber ?? undefined, changelogUrl: finding.changelogUrl || undefined, advisories: finding.advisories || [], sourceLine: finding.sourceLine ?? undefined, introducedCommit: finding.introducedCommit || '', introducedCommitUrl: finding.introducedCommitUrl || undefined, assignee: finding.assignee || '', dueAt: finding.dueAt, overdue: Boolean(finding.overdue) }
}

export async function loadRepositories(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Repository[] & { total?: number }> { const rows = await loadAllPages<any>('/api/v1/repositories', token, fetcher); return Object.assign(rows.map(normalizeRepository), { total: rows.total }) }
export async function loadRepositoryLanguages(id: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Record<string, number>> { const result = await request(`/api/v1/repositories/${repoId(id)}/languages`, { token }, fetcher); return result?.items || result?.Items || {} }
export async function loadRuns(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Execution[] & { total?: number }> { const rows = await loadAllPages<any>('/api/v1/runs', token, fetcher); return Object.assign(rows.map(normalizeExecution), { total: rows.total }) }
export async function loadFindings(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Finding[] & { total?: number }> { const rows = await loadAllPages<any>('/api/v1/findings', token, fetcher); return Object.assign(rows.map(normalizeFinding), { total: rows.total }) }
export async function loadSettings(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Record<string, any>> { return request('/api/v1/settings', { token }, fetcher) }
export async function loadAudit(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<any[]> { return loadAllPages<any>('/api/v1/audit', token, fetcher) }
export async function loadScanners(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<ScannerDefinition[]> { return items<any>(await request('/api/v1/scanners', { token }, fetcher)).map(normalizeScanner) }

export async function queueScan({ repositoryId, scanner, mode, token }: { repositoryId: string; scanner: string; mode: string; token?: string }, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<Execution> { return normalizeExecution(await request('/api/v1/runs', { method: 'POST', token, body: JSON.stringify({ repositoryId, scanner, mode }) }, fetcher)) }
export async function cancelRun(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/runs/${encodeURIComponent(id)}`, { method: 'DELETE', token }, fetcher) }
export async function loadRunLogs(id: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = '') { return request(`/api/v1/runs/${encodeURIComponent(id)}/logs`, { token }, fetcher) }
export async function createRepository(repository: Record<string, any>, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<Repository> { return normalizeRepository(await request('/api/v1/repositories', { method: 'POST', token, body: JSON.stringify(repository) }, fetcher)) }
export async function deleteRepository(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/repositories/${repoId(id)}`, { method: 'DELETE', token }, fetcher) }
export async function updateSettings(settings: Record<string, any>, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request('/api/v1/settings', { method: 'PUT', token, body: JSON.stringify(settings) }, fetcher) }
export function normalizeScanner(scanner: any): ScannerDefinition { return { id: scanner.id || scanner.ID, name: scanner.name || scanner.Name, image: scanner.image || scanner.Image || '', command: scanner.command || scanner.Command || [], args: scanner.args || scanner.Args || [], builtIn: Boolean(scanner.builtIn ?? scanner.BuiltIn), enabled: scanner.enabled !== false && scanner.Enabled !== false, outputFormat: scanner.outputFormat || 'none', category: scanner.category || 'other', successExitCodes: scanner.successExitCodes || [0] } }
export async function createScanner(scanner: Omit<ScannerDefinition, 'builtIn'> & { builtIn?: boolean }, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<ScannerDefinition> { return normalizeScanner(await request('/api/v1/scanners', { method: 'POST', token, body: JSON.stringify(scanner) }, fetcher)) }
export async function updateScanner(id: string, scanner: Omit<ScannerDefinition, 'id' | 'builtIn'> & { builtIn?: boolean }, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<ScannerDefinition> { return normalizeScanner(await request(`/api/v1/scanners/${encodeURIComponent(id)}`, { method: 'PUT', token, body: JSON.stringify({ ...scanner, id }) }, fetcher)) }
export type ParsePreviewResult = { error?: string | null; authoritative: boolean; output: string; findings: { detector: string; severity: string; path: string; line?: number; detail: string; helpUrl?: string; preview?: string; verified: boolean }[] }
export async function previewParse(format: string, output: string, category: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<ParsePreviewResult> { return request('/api/v1/scanners/preview-parse', { method: 'POST', token, body: JSON.stringify({ format, output, category }) }, fetcher) }
export async function deleteScanner(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/scanners/${encodeURIComponent(id)}`, { method: 'DELETE', token }, fetcher) }

export async function triageFinding(fingerprint: string, state: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher, reason?: string): Promise<Finding> { return normalizeFinding(await request(`/api/v1/findings/${encodeURIComponent(fingerprint)}`, { method: 'PATCH', token, body: JSON.stringify(reason ? { state, reason } : { state }) }, fetcher)) }
export async function loadFindingHistory(fingerprint: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<FindingHistoryItem[]> { return items<FindingHistoryItem>(await request(`/api/v1/findings/${encodeURIComponent(fingerprint)}/history`, { token }, fetcher)) }
export async function unblockRepository(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<Repository> { return normalizeRepository(await request(`/api/v1/repositories/${repoId(id)}/unblock`, { method: 'POST', token }, fetcher)) }
export async function loadSystem(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<SystemInfo> { return request('/api/v1/system', { token }, fetcher) }
export async function loadRoleMappings(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<RoleMapping[]> { return items<RoleMapping>(await request('/api/v1/role-mappings', { token }, fetcher)) }
export async function saveRoleMapping(idpGroup: string, aegisRole: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<RoleMapping> { return request(`/api/v1/role-mappings/${encodeURIComponent(idpGroup)}`, { method: 'PUT', token, body: JSON.stringify({ aegisRole }) }, fetcher) }
export async function deleteRoleMapping(idpGroup: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/role-mappings/${encodeURIComponent(idpGroup)}`, { method: 'DELETE', token }, fetcher) }

export async function loadFindingStats(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<FindingStats> { return request('/api/v1/findings/stats', { token }, fetcher) }
export async function assignFinding(fingerprint: string, assignee: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<Finding> { return normalizeFinding(await request(`/api/v1/findings/${encodeURIComponent(fingerprint)}/assignee`, { method: 'PUT', token, body: JSON.stringify({ assignee }) }, fetcher)) }
export async function loadAlertAcks(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<AlertAck[]> { return items<AlertAck>(await request('/api/v1/alerts/acks', { token }, fetcher)) }
export async function saveAlertAck(key: string, snoozeHours: number | null, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request('/api/v1/alerts/acks', { method: 'PUT', token, body: JSON.stringify(snoozeHours ? { key, snoozeHours } : { key }) }, fetcher) }
export async function deleteAlertAck(key: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/alerts/acks?key=${encodeURIComponent(key)}`, { method: 'DELETE', token }, fetcher) }
export async function testWebhook(token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<{ ok: boolean; detail: string }> { return request('/api/v1/webhook/test', { method: 'POST', token }, fetcher) }
export async function loadPolicies(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<PolicyItem[]> { return items<PolicyItem>(await request('/api/v1/policies', { token }, fetcher)) }

export type SbomSummary = { id: string; kind: 'repository' | 'image'; ref: string; origin: string; source: string; commitSHA: string; generatedAt: string; componentCount: number; critical: number; high: number; medium: number; low: number; error: string; hasDocument: boolean }
export type SbomVulnerability = { id: string; severity: string; fix: string; url: string; summary: string }
export type SbomComponent = { name: string; version: string; purl: string; license: string; vulnerabilities: SbomVulnerability[] }
export type SbomGroup = { name: string; ecosystem: string; components: SbomComponent[] }
export type SbomDetail = SbomSummary & { groups: SbomGroup[] }
export async function loadSboms(id: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<{ images: string[]; artifacts: SbomSummary[] }> { const r = await request(`/api/v1/repositories/${repoId(id)}/sboms`, { token }, fetcher); return { images: r?.images || [], artifacts: r?.artifacts || [] } }
export async function loadSbom(id: string, sbomId: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<SbomDetail> { return request(`/api/v1/repositories/${repoId(id)}/sboms/${encodeURIComponent(sbomId)}`, { token }, fetcher) }
export async function saveRepositoryImages(id: string, images: string[], token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<string[]> { return (await request(`/api/v1/repositories/${repoId(id)}/images`, { method: 'PUT', token, body: JSON.stringify({ images }) }, fetcher))?.images || [] }
/** Saves the stored CycloneDX document as a file (the endpoint needs the bearer token, so a plain link cannot be used). */
export async function downloadSbom(id: string, sbom: Pick<SbomSummary, 'id' | 'ref' | 'kind'>, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) {
  const response = await fetcher(`${baseUrl()}/api/v1/repositories/${repoId(id)}/sboms/${encodeURIComponent(sbom.id)}/cyclonedx`, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
  if (!response.ok) throw new Error(`SBOM download failed: ${response.status}`)
  const url = URL.createObjectURL(await response.blob())
  const link = Object.assign(document.createElement('a'), { href: url, download: `sbom-${(sbom.kind === 'image' ? sbom.ref : id).replace(/[^A-Za-z0-9._-]/g, '_')}.cdx.json` })
  link.click(); URL.revokeObjectURL(url)
}

export type ActivityRun = { id: string; repositoryId: string; scanner: string; mode: string; status: string; attempt: number; createdAt: string; startedAt?: string; finishedAt?: string; notBefore?: string; findingCount: number; outputInterpreted: boolean; jobName?: string; commitSHA: string }
export type Activity = { scanners: { id: string; name: string; category: string }[]; latest: ActivityRun[]; active: ActivityRun[]; open: { repositoryId: string; scanner: string; count: number }[]; /** Open findings by severity; only filled when a single repository was requested. */ severities?: { scanner: string; severity: string; count: number }[]; generatedAt: string }
/** Latest run per repository and scanner, open finding counts and runs in flight; pass a repository id to narrow it to one repository. */
export async function loadActivity(fetcher: Fetcher = fetch as unknown as Fetcher, token = '', repositoryId?: string): Promise<Activity> { return request(`/api/v1/activity${repositoryId ? `?repositoryId=${encodeURIComponent(repositoryId)}` : ''}`, { token }, fetcher) }

export type DashboardRepo = { id: string; name: string; blocked: boolean; verified: number; critical: number; high: number; medium: number; low: number; categories: Record<string, number> }
export type DashboardCve = { id: string; severity: string; repos: number; occurrences: number; package: string; fixAvailable: boolean; repoIds: string[]; firstSeen: string }
export type Dashboard = { days: number; totals: { critical: number; high: number; medium: number; low: number; verifiedSecrets: number; reposWithCritical: number; cvesTotal: number; newCves: number }; repos: DashboardRepo[]; newCves: DashboardCve[]; widespread: DashboardCve[]; generatedAt: string }
/** Server-side aggregates over every open finding: riskiest repositories, new CVEs (excluding each repository's baseline scan) and the most widespread ones. */
export async function loadDashboard(days: number, fetcher: Fetcher = fetch as unknown as Fetcher, token = '', repositoryIds: string[] | null = null): Promise<Dashboard> {
  const d = await request(`/api/v1/dashboard?days=${days}${scopeQuery(repositoryIds)}`, { token }, fetcher)
  const cve = (c: any): DashboardCve => ({ id: c.id ?? c.Id, severity: c.severity ?? c.Severity, repos: c.repos ?? c.Repos, occurrences: c.occurrences ?? c.Occurrences, package: c.package ?? c.Package, fixAvailable: c.fixAvailable ?? c.FixAvailable ?? false, repoIds: c.repoIds ?? c.RepoIds ?? [], firstSeen: c.firstSeen ?? c.FirstSeen })
  return { ...d, newCves: d.newCves.map(cve), widespread: d.widespread.map(cve) }
}

/** "&repositoryId=a&repositoryId=b" for a watch-list scope; empty (= every repository) when the scope is null. */
export const scopeQuery = (ids: string[] | null) => ids?.length ? ids.map(i => `&repositoryId=${encodeURIComponent(i)}`).join('') : ''

export type Fix = { kind: 'image' | 'package' | 'secret' | 'update' | 'issue'; repositoryId: string; repositoryName: string; title: string; where: string; action: string; severity: string; critical: number; high: number; medium: number; low: number; items: number; fixable: number; verified: boolean; scanner: string }
export type Fixes = { total: number; shown: number; fixes: Fix[]; totals: { critical: number; high: number; medium: number; low: number; verified: number } }
const camel = (o: any): any => Array.isArray(o) ? o.map(camel) : o && typeof o === 'object' ? Object.fromEntries(Object.entries(o).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), camel(v)])) : o
/** Open findings grouped into actions (update this image, upgrade this package, rotate this secret), worst first. */
export async function loadFixes(repositoryIds: string[] | null, fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Fixes> { return camel(await request(`/api/v1/fixes?${scopeQuery(repositoryIds).slice(1)}`, { token }, fetcher)) }

export type RepositoryRisk = { id: string; blocked: boolean; verified: number; critical: number; high: number; medium: number; low: number; lastScanAt?: string | null }
/** Severity split, verified secrets and last successful scan for every repository (list views). */
export async function loadRepositoryRisk(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<RepositoryRisk[]> { return items<RepositoryRisk>(await request('/api/v1/risk/repositories', { token }, fetcher)) }
