import { expect, test } from '@playwright/test'
import { loginAs } from './login'

// These need the real API (scanner CRUD, parse preview, findings produced by scripts/custom-scanners-e2e.sh): compose run only.
test.beforeEach(({ baseURL }) => { test.skip(!baseURL?.includes(':3000') && !process.env.COMPOSE_BASE_URL, 'custom scanner flows need the live API (npm run test:e2e:compose)') })

const SARIF = JSON.stringify({
  version: '2.1.0',
  runs: [{
    tool: { driver: { name: 'demo', rules: [{ id: 'DEMO-1', shortDescription: { text: 'Hardcoded token' }, helpUri: 'https://example.com/rules/DEMO-1', properties: { 'security-severity': '8.1' } }] } },
    results: [{ ruleId: 'DEMO-1', ruleIndex: 0, message: { text: 'token assigned to a string literal' }, locations: [{ physicalLocation: { artifactLocation: { uri: 'src/app.py' }, region: { startLine: 12, snippet: { text: 'token = "ghp_exampleexampleexample"' } } } }] }],
  }],
})

test('admin adds a SARIF scanner, previews the parse, and it appears in the sidebar', async ({ page }) => {
  await loginAs(page, 'admin', '/?view=Add scanner')
  await expect(page.getByRole('heading', { name: 'Add custom container scanner' })).toBeVisible()
  await page.locator('.add-scanner').getByLabel('Custom scanner ID').fill('pw-sarif')
  await page.getByLabel('Custom scanner name').fill('PW SARIF')
  await page.getByLabel('Custom scanner image').fill('alpine:3.20')
  await page.getByLabel('Custom scanner output format').selectOption('sarif')
  await page.getByLabel('Custom scanner category').selectOption('code')
  await page.getByLabel('Custom scanner success exit codes').fill('0, 1')

  const add = page.locator('.add-scanner')
  await add.getByText('Preview parsing').click()
  await add.getByLabel('Sample scanner output').fill(SARIF)
  await add.getByRole('button', { name: 'Parse sample' }).click()
  const row = add.locator('.preview-table tbody tr')
  await expect(row).toHaveCount(1)
  await expect(row).toContainText('DEMO-1')
  await expect(row).toContainText('high')
  await expect(row).toContainText('src/app.py:12')
  await expect(row.getByRole('link', { name: 'DEMO-1' })).toHaveAttribute('href', 'https://example.com/rules/DEMO-1')
  await expect(add.locator('.preview-table')).not.toContainText('ghp_exampleexampleexample') // raw snippets never come back
  await page.screenshot({ path: '../screenshots/custom-scanner-preview.png', fullPage: true })

  await page.getByRole('button', { name: /Add custom scanner/ }).click()
  await expect(page.locator('.sub-nav').getByRole('button', { name: 'PW SARIF' })).toBeVisible()
  await page.locator('.sub-nav').getByRole('button', { name: 'PW SARIF' }).click()
  await expect(page.locator('h2', { hasText: 'PW SARIF' })).toBeVisible()
  await page.locator('.view-tabs').getByRole('button', { name: 'Settings' }).click()
  await expect(page.getByLabel('pw-sarif output format')).toHaveValue('sarif')
  await expect(page.getByLabel('pw-sarif success exit codes')).toHaveValue('0, 1')

  // cleanup (shared compose data)
  await page.getByRole('button', { name: 'Remove' }).click()
  await expect(page.locator('.sub-nav').getByRole('button', { name: 'PW SARIF' })).toHaveCount(0)
})

test('unparseable sample is reported, not silently empty', async ({ page }) => {
  await loginAs(page, 'admin', '/?view=Add scanner')
  const add = page.locator('.add-scanner')
  await add.getByText('Preview parsing').click()
  await add.getByLabel('Sample scanner output').fill('definitely not sarif')
  await add.getByRole('button', { name: 'Parse sample' }).click()
  await expect(add.getByRole('alert')).toContainText('could not interpret')
})

test('findings from custom scanners show their rule, masked preview and forge link', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Findings')
  await expect(page.getByText(/\d+ matching/)).toBeVisible()
  const scanner = page.getByLabel('Filter by scanner')
  const hasGitleaks = await scanner.locator('option[value="gitleaks"]').waitFor({ state: 'attached', timeout: 8000 }).then(() => true).catch(() => false)
  test.skip(!hasGitleaks, 'run scripts/custom-scanners-e2e.sh first')
  await scanner.selectOption('gitleaks')
  const first = page.locator('.finding-row').first()
  await expect(first).toBeVisible()
  await first.click()
  const details = page.locator('.finding-details')
  await expect(details.locator('.finding-preview')).toContainText('private-key')
  await expect(details.getByText('Scanner').locator('..')).toContainText('Gitleaks')
  await expect(details.getByText('Code snippets are never stored in full')).toBeVisible()
  await expect(details.locator('.preview-file a')).toHaveAttribute('href', /keys/)
  await page.screenshot({ path: '../screenshots/custom-scanner-finding-gitleaks.png', fullPage: true })

  await scanner.selectOption('aegis-demo')
  if (!(await page.locator('.finding-details').isVisible())) await page.locator('.finding-row').first().click()
  await expect(page.locator('.finding-details').getByRole('link', { name: /documentation for/ })).toHaveAttribute('href', 'https://example.com/rules/demo-keyword')
  await page.screenshot({ path: '../screenshots/custom-scanner-finding-aegis-json.png', fullPage: true })
})

test('a scanner with output format none is flagged "Not interpreted", never "0"', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Scans')
  const badge = page.getByText('Not interpreted').first()
  test.skip(!(await badge.waitFor({ state: 'visible', timeout: 5000 }).then(() => true).catch(() => false)), 'run scripts/custom-scanners-e2e.sh first')
  const silentRow = page.locator('.feature-row', { hasText: 'Silent' }).first()
  await expect(silentRow).toContainText('Not interpreted')
  await expect(silentRow).not.toContainText(/\b0\b/)
  await page.screenshot({ path: '../screenshots/custom-scanner-not-interpreted.png', fullPage: true })
})
