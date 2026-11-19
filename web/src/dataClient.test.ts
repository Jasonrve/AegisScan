/** @vitest-environment jsdom */
import { describe, expect, it } from 'vitest'
import { deleteRepository, assignFinding, deleteAlertAck, loadFindings, loadPolicies, loadRuns, loadRepositories, queueScan, saveAlertAck, testWebhook, triageFinding } from './dataClient'

describe('API data client', () => {
  it('normalizes stable repository DTOs for live views', async () => {
    const rows = await loadRepositories(async () => ({ ok: true, json: async () => [{ name: 'platform/api', activeFindingCount: 2 }] }))
    expect(rows[0]).toMatchObject({ name: 'platform/api', activeFindingCount: 2, status: 'Ready' })
  })

  it('queues a scan with the server contract', async () => {
    let request
    const execution = await queueScan({ repositoryId: 'repo-1', scanner: 'trufflehog', mode: 'full' }, async (url, options) => {
      request = { url, options }
      return { ok: true, json: async () => ({ status: 'queued' }) }
    })
    expect(execution).toMatchObject({ status: 'queued' })
    expect(JSON.parse(request.options.body)).toEqual({ repositoryId: 'repo-1', scanner: 'trufflehog', mode: 'full' })
  })

  it('keeps server-computed severity and finding details', async () => {
    const [finding] = await loadFindings(async () => ({ ok: true, json: async () => ({ items: [{ fingerprint: 'f1', repositoryId: 'r', detector: 'AWS', verified: true, severity: 'critical', scanner: 'trufflehog', detail: 'line 3', state: 'open' }] }) }))
    expect(finding).toMatchObject({ severity: 'critical', scanner: 'trufflehog', detail: 'line 3' })
  })

  it('requests the API maximum page so dashboard counts are not capped at 50 rows', async () => {
    let url = ''
    await loadRuns(async (u) => { url = u; return { ok: true, json: async () => ({ items: [] }) } })
    expect(url).toContain('pageSize=200')
  })

  it('triages a finding with PATCH and the new state', async () => {
    let request
    const updated = await triageFinding('abc', 'ignored', 'tok', async (url, options) => { request = { url, options }; return { ok: true, json: async () => ({ fingerprint: 'abc', state: 'ignored' }) } }, 'accepted risk')
    expect(request.options.method).toBe('PATCH')
    expect(JSON.parse(request.options.body)).toEqual({ state: 'ignored', reason: 'accepted risk' })
    expect(updated.state).toBe('ignored')
  })

  it('acknowledges and snoozes alerts through the acks endpoint', async () => {
    const calls: any[] = []
    const fetcher = async (url, options) => { calls.push({ url, options }); return { ok: true, status: 200, json: async () => ({}) } }
    await saveAlertAck('run:1', 24, 'tok', fetcher)
    await saveAlertAck('run:2', null, 'tok', fetcher)
    await deleteAlertAck('run:1', 'tok', fetcher)
    expect(JSON.parse(calls[0].options.body)).toEqual({ key: 'run:1', snoozeHours: 24 })
    expect(JSON.parse(calls[1].options.body)).toEqual({ key: 'run:2' })
    expect(calls[2].url).toContain('/alerts/acks?key=run%3A1')
    expect(calls[2].options.method).toBe('DELETE')
  })

  it('assigns a finding and reads policies and webhook test results', async () => {
    let request
    const finding = await assignFinding('abc', 'alice', 'tok', async (url, options) => { request = { url, options }; return { ok: true, json: async () => ({ fingerprint: 'abc', assignee: 'alice', overdue: true }) } })
    expect(request.url).toContain('/findings/abc/assignee')
    expect(JSON.parse(request.options.body)).toEqual({ assignee: 'alice' })
    expect(finding).toMatchObject({ assignee: 'alice', overdue: true })
    expect(await loadPolicies(async () => ({ ok: true, json: async () => ({ items: [{ name: 'default' }] }) }))).toHaveLength(1)
    expect(await testWebhook('tok', async () => ({ ok: true, json: async () => ({ ok: true, detail: 'HTTP 200' }) }))).toEqual({ ok: true, detail: 'HTTP 200' })
  })

  it('sends slash repository ids as ~ so they fit in one path segment', async () => {
    let url = ''
    await deleteRepository('team/edge-service', 'tok', async (u) => { url = u; return { ok: true, status: 204, json: async () => null } })
    expect(url).toContain('/api/v1/repositories/team~edge-service')
  })

  it('loads every page of a list, not just the newest 200 rows', async () => {
    const urls: string[] = []
    const rows = await loadFindings(async (url) => {
      urls.push(url)
      const page = Number(/page=(\d+)/.exec(url)![1])
      return { ok: true, json: async () => ({ total: 450, items: Array.from({ length: page < 3 ? 200 : 50 }, (_, i) => ({ fingerprint: `f${page}-${i}`, repositoryId: 'r' })) }) }
    })
    expect(urls).toHaveLength(3)
    expect(rows).toHaveLength(450)
    expect(rows.total).toBe(450)
  })
})
