import { describe, expect, it } from 'vitest'
import { filterRepositories } from './repositorySearch'

describe('repository search', () => {
  it('matches names case-insensitively and ignores surrounding whitespace', () => {
    const rows = [['platform/api'], ['web/app'], ['infra/terraform']]
    expect(filterRepositories(rows, '  API ')).toEqual([['platform/api']])
  })

  it('returns all rows for an empty query', () => {
    const rows = [['platform/api'], ['web/app']]
    expect(filterRepositories(rows, '')).toEqual(rows)
  })
})
