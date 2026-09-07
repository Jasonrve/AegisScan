import type { Page } from '@playwright/test'

const credentials: Record<string, { username: string; password: string }> = {
  admin: { username: 'admin', password: 'admin123' },
  operator: { username: 'operator', password: 'operator123' },
  viewer: { username: 'viewer', password: 'viewer123' },
}

// Fixture mode (the default `npm run test:e2e` run) never shows a login gate — it renders the app
// directly against canned data, gated only by the `?role=` query param. The compose run
// (`npm run test:e2e:compose`, real backend + real Keycloak) does gate on auth, so this only fills
// in AegisScan's own username/password form (the direct-login option, an alternative to the SSO
// redirect button on the same screen) when we actually land on it.
export async function loginAs(page: Page, role: 'admin' | 'operator' | 'viewer', path = '/') {
  await page.goto(`${path}${path.includes('?') ? '&' : '?'}role=${role}`)
  const { username, password } = credentials[role]
  const usernameField = page.getByLabel('Username')
  if (await usernameField.isVisible({ timeout: 3000 }).catch(() => false)) {
    await usernameField.fill(username)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /Sign in with username and password/ }).click()
  }
}
