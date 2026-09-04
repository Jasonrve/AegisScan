import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  timeout: 15000,
  use: { baseURL: process.env.COMPOSE_BASE_URL || 'http://127.0.0.1:3000', ...devices['Desktop Chrome'] },
})
