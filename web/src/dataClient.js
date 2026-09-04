const baseUrl = () => import.meta.env?.VITE_API_BASE_URL || ''

async function request(path, options = {}, fetcher = fetch) {
  const response = await fetcher(`${baseUrl()}${path}`, { headers: { Accept: 'application/json', ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(options.role ? { 'X-Aegis-Role': options.role } : {}) }, ...options })
  if (!response.ok) throw new Error(`${path} request failed: ${response.status}`)
  return response.status === 204 ? null : response.json()
}
const items = payload => Array.isArray(payload) ? payload : (payload?.items || payload?.Items || [])

export function normalizeRepository(repository) {
  return { id: repository.id || repository.ID, name: repository.name || repository.Name, provider: repository.provider || repository.Provider || 'github', cloneUrl: repository.cloneUrl || repository.CloneURL || '', defaultBranch: repository.defaultBranch || repository.DefaultBranch || 'main', status: repository.status || repository.Status || 'Ready', cacheState: repository.cacheState || repository.CacheState || 'Unknown', activeFindingCount: Number(repository.activeFindingCount ?? repository.ActiveFindingCount ?? 0), latestCommitSHA: repository.latestCommitSHA || repository.LatestCommitSHA || '' }
}
export function normalizeExecution(execution) {
  return { id: execution.id || execution.ID, repositoryId: execution.repositoryId || execution.RepositoryID, scanner: execution.scanner || execution.Scanner, mode: execution.mode || execution.Mode, status: execution.status || execution.Status, attempt: execution.attempt || execution.Attempt || 1, startedAt: execution.startedAt || execution.StartedAt, finishedAt: execution.finishedAt || execution.FinishedAt, errorCode: execution.errorCode || execution.ErrorCode || '', summary: execution.summary || execution.Summary || {}, sanitizedLog: execution.sanitizedLog || execution.SanitizedLog || '' }
}
export function normalizeFinding(finding) {
  return { fingerprint: finding.fingerprint || finding.Fingerprint, repositoryId: finding.repositoryId || finding.RepositoryID, detector: finding.detector || finding.Detector || 'Unknown detector', verified: Boolean(finding.verified ?? finding.Verified), safePath: finding.safePath || finding.SafePath || '', commitSHA: finding.commitSHA || finding.CommitSHA || '', firstSeen: finding.firstSeen || finding.FirstSeen, lastSeen: finding.lastSeen || finding.LastSeen, state: finding.state || finding.State || 'open' }
}

export async function loadRepositories(fetcher = fetch, role = '') { return items(await request('/api/v1/repositories', { role }, fetcher)).map(normalizeRepository) }
export async function loadRuns(fetcher = fetch, role = '') { return items(await request('/api/v1/runs', { role }, fetcher)).map(normalizeExecution) }
export async function loadFindings(fetcher = fetch, role = '') { return items(await request('/api/v1/findings', { role }, fetcher)).map(normalizeFinding) }
export async function loadSettings(fetcher = fetch, role = '') { return request('/api/v1/settings', { role }, fetcher) }
export async function loadAudit(fetcher = fetch, role = '') { return items(await request('/api/v1/audit', { role }, fetcher)) }

export async function queueScan({ repositoryId, scanner, mode, role }, fetcher = fetch) { return normalizeExecution(await request('/api/v1/runs', { method: 'POST', role, body: JSON.stringify({ repositoryId, scanner, mode }) }, fetcher)) }
export async function cancelRun(id, role, fetcher = fetch) { return request(`/api/v1/runs/${encodeURIComponent(id)}`, { method: 'DELETE', role }, fetcher) }
export async function loadRunLogs(id, fetcher = fetch, role = '') { return request(`/api/v1/runs/${encodeURIComponent(id)}/logs`, { role }, fetcher) }
export async function createRepository(repository, role, fetcher = fetch) { return normalizeRepository(await request('/api/v1/repositories', { method: 'POST', role, body: JSON.stringify(repository) }, fetcher)) }
export async function deleteRepository(id, role, fetcher = fetch) { return request(`/api/v1/repositories/${encodeURIComponent(id)}`, { method: 'DELETE', role }, fetcher) }
export async function updateSettings(settings, role, fetcher = fetch) { return request('/api/v1/settings', { method: 'PUT', role, body: JSON.stringify(settings) }, fetcher) }
