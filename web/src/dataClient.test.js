import { describe, expect, it } from 'vitest'
import { loadRepositories, queueScan } from './dataClient'

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
})
