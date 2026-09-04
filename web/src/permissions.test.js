import { describe, expect, it } from 'vitest'
import { canConfigure, canRunScans } from './permissions'

describe('role permissions', () => {
  it('allows admins and operators to run scans', () => {
    expect(canRunScans('admin')).toBe(true)
    expect(canRunScans('operator')).toBe(true)
    expect(canRunScans('viewer')).toBe(false)
  })

  it('limits configuration to administrators', () => {
    expect(canConfigure('ADMIN')).toBe(true)
    expect(canConfigure('operator')).toBe(false)
    expect(canConfigure('viewer')).toBe(false)
  })
})
