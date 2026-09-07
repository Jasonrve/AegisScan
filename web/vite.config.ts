import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  server: {
    proxy: { '/api': process.env.VITE_DEV_API_PROXY_TARGET || 'http://127.0.0.1:8080', '/healthz': process.env.VITE_DEV_API_PROXY_TARGET || 'http://127.0.0.1:8080', '/readyz': process.env.VITE_DEV_API_PROXY_TARGET || 'http://127.0.0.1:8080', '/metrics': process.env.VITE_DEV_API_PROXY_TARGET || 'http://127.0.0.1:8080' },
    // Bind-mounted volumes (Docker on Windows/macOS) don't propagate native file-change events
    // reliably, so the dev-container compose override sets this to fall back to polling.
    watch: process.env.VITE_DOCKER_POLLING === 'true' ? { usePolling: true } : undefined,
  },
  define: { 'import.meta.env.VITE_FIXTURE_MODE': JSON.stringify(mode === 'test' || process.env.VITE_FIXTURE_MODE === 'true' ? 'true' : 'false') },
  test: { exclude: ['e2e/**', 'node_modules/**'] },
}))
