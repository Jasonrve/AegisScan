/** @vitest-environment jsdom */
import '@testing-library/jest-dom/vitest'
import { render, screen, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { App } from './main'

afterEach(() => {
  cleanup()
  window.history.replaceState({}, '', '/')
})

describe('dashboard permissions', () => {
  it('hides scan actions for viewers', () => {
    window.history.replaceState({}, '', '/?role=viewer')
    render(<App />)
    expect(screen.queryByRole('button', { name: 'Run Scan' })).not.toBeInTheDocument()
  })

  it('shows scan actions for operators', () => {
    window.history.replaceState({}, '', '/?role=operator')
    render(<App />)
    expect(screen.getByRole('button', { name: 'Run Scan' })).toBeInTheDocument()
  })
})
