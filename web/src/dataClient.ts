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
}

export type Execution = {
  id: string
  repositoryId: string
  scanner: string
  mode?: string
  status: string
  attempt: number
  startedAt?: string
  finishedAt?: string
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
}

export type ScannerDefinition = {
  id: string
  name: string
  image: string
  command: string[]
  args: string[]
  builtIn: boolean
  enabled: boolean
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
const items = <T,>(payload: any): T[] => Array.isArray(payload) ? payload : (payload?.items || payload?.Items || [])

export function normalizeRepository(repository: any): Repository {
  return { id: repository.id || repository.ID, name: repository.name || repository.Name, provider: repository.provider || repository.Provider || 'github', cloneUrl: repository.cloneUrl || repository.CloneURL || '', defaultBranch: repository.defaultBranch || repository.DefaultBranch || 'main', status: repository.status || repository.Status || 'Ready', cacheState: repository.cacheState || repository.CacheState || 'Unknown', activeFindingCount: Number(repository.activeFindingCount ?? repository.ActiveFindingCount ?? 0), latestCommitSHA: repository.latestCommitSHA || repository.LatestCommitSHA || '' }
}
export function normalizeExecution(execution: any): Execution {
  return { id: execution.id || execution.ID, repositoryId: execution.repositoryId || execution.RepositoryID, scanner: execution.scanner || execution.Scanner, mode: execution.mode || execution.Mode, status: execution.status || execution.Status, attempt: execution.attempt || execution.Attempt || 1, startedAt: execution.startedAt || execution.StartedAt, finishedAt: execution.finishedAt || execution.FinishedAt, errorCode: execution.errorCode || execution.ErrorCode || '', summary: execution.summary || execution.Summary || {}, sanitizedLog: execution.sanitizedLog || execution.SanitizedLog || '', links: execution.links || execution.Links || [] }
}
export function normalizeFinding(finding: any): Finding {
  return { fingerprint: finding.fingerprint || finding.Fingerprint, repositoryId: finding.repositoryId || finding.RepositoryID, detector: finding.detector || finding.Detector || 'Unknown detector', verified: Boolean(finding.verified ?? finding.Verified), safePath: finding.safePath || finding.SafePath || '', commitSHA: finding.commitSHA || finding.CommitSHA || '', firstSeen: finding.firstSeen || finding.FirstSeen, lastSeen: finding.lastSeen || finding.LastSeen, state: finding.state || finding.State || 'open' }
}

export async function loadRepositories(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Repository[]> { return items<any>(await request('/api/v1/repositories', { token }, fetcher)).map(normalizeRepository) }
export async function loadRepositoryLanguages(id: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Record<string, number>> { const result = await request(`/api/v1/repositories/${encodeURIComponent(id)}/languages`, { token }, fetcher); return result?.items || result?.Items || {} }
export async function loadRuns(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Execution[]> { return items<any>(await request('/api/v1/runs', { token }, fetcher)).map(normalizeExecution) }
export async function loadFindings(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Finding[]> { return items<any>(await request('/api/v1/findings', { token }, fetcher)).map(normalizeFinding) }
export async function loadSettings(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<Record<string, any>> { return request('/api/v1/settings', { token }, fetcher) }
export async function loadAudit(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<any[]> { return items<any>(await request('/api/v1/audit', { token }, fetcher)) }
export async function loadScanners(fetcher: Fetcher = fetch as unknown as Fetcher, token = ''): Promise<ScannerDefinition[]> { return items<any>(await request('/api/v1/scanners', { token }, fetcher)).map(normalizeScanner) }

export async function queueScan({ repositoryId, scanner, mode, token }: { repositoryId: string; scanner: string; mode: string; token?: string }, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<Execution> { return normalizeExecution(await request('/api/v1/runs', { method: 'POST', token, body: JSON.stringify({ repositoryId, scanner, mode }) }, fetcher)) }
export async function cancelRun(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/runs/${encodeURIComponent(id)}`, { method: 'DELETE', token }, fetcher) }
export async function loadRunLogs(id: string, fetcher: Fetcher = fetch as unknown as Fetcher, token = '') { return request(`/api/v1/runs/${encodeURIComponent(id)}/logs`, { token }, fetcher) }
export async function createRepository(repository: Record<string, any>, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<Repository> { return normalizeRepository(await request('/api/v1/repositories', { method: 'POST', token, body: JSON.stringify(repository) }, fetcher)) }
export async function deleteRepository(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/repositories/${encodeURIComponent(id)}`, { method: 'DELETE', token }, fetcher) }
export async function updateSettings(settings: Record<string, any>, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request('/api/v1/settings', { method: 'PUT', token, body: JSON.stringify(settings) }, fetcher) }
export function normalizeScanner(scanner: any): ScannerDefinition { return { id: scanner.id || scanner.ID, name: scanner.name || scanner.Name, image: scanner.image || scanner.Image || '', command: scanner.command || scanner.Command || [], args: scanner.args || scanner.Args || [], builtIn: Boolean(scanner.builtIn ?? scanner.BuiltIn), enabled: scanner.enabled !== false && scanner.Enabled !== false } }
export async function createScanner(scanner: Omit<ScannerDefinition, 'builtIn'> & { builtIn?: boolean }, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<ScannerDefinition> { return normalizeScanner(await request('/api/v1/scanners', { method: 'POST', token, body: JSON.stringify(scanner) }, fetcher)) }
export async function updateScanner(id: string, scanner: Omit<ScannerDefinition, 'id' | 'builtIn'> & { builtIn?: boolean }, token: string, fetcher: Fetcher = fetch as unknown as Fetcher): Promise<ScannerDefinition> { return normalizeScanner(await request(`/api/v1/scanners/${encodeURIComponent(id)}`, { method: 'PUT', token, body: JSON.stringify({ ...scanner, id }) }, fetcher)) }
export async function deleteScanner(id: string, token: string, fetcher: Fetcher = fetch as unknown as Fetcher) { return request(`/api/v1/scanners/${encodeURIComponent(id)}`, { method: 'DELETE', token }, fetcher) }
