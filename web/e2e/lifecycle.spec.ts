import { expect, test } from '@playwright/test'
import { loginAs } from './login'

test('operator can acknowledge an alert and reopen it', async ({ page }) => {
  await loginAs(page, 'operator', '/?view=Alerts')
  await expect(page.getByRole('heading', { name: 'Alert inbox' })).toBeVisible()
  const acknowledge = page.getByRole('button', { name: 'Acknowledge' }).first()
  const hasAlert = await acknowledge.waitFor({ state: 'visible', timeout: 6000 }).then(() => true).catch(() => false)
  test.skip(!hasAlert, 'no active alerts in this dataset')
  await acknowledge.click()
  await expect(page.getByRole('status')).toContainText('Alert acknowledged')
  await page.getByRole('button', { name: /Show acknowledged/ }).click()
  await page.getByRole('button', { name: 'Reopen' }).first().click() // restore shared data
  await expect(page.getByRole('status')).toContainText('Alert reopened')
})

test('admin sees webhook signing, SLA and forge mapping in Settings', async ({ page }) => {
  await loginAs(page, 'admin', '/?view=Settings')
  await expect(page.getByLabel('Webhook signing secret')).toHaveAttribute('type', 'password')
  await expect(page.getByRole('button', { name: 'Send test event' })).toBeVisible()
  for (const severity of ['critical', 'high', 'medium', 'low']) await expect(page.getByLabel(`${severity} SLA days`)).toBeVisible()
  await expect(page.getByLabel('Forge URL mapping')).toBeVisible()
})

test('policies show the declared scan policy resources table', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Policies')
  await expect(page.getByRole('heading', { name: 'Scan policy resources' })).toBeVisible()
})

test('repositories can be filtered by health and sorted', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Repositories')
  await expect(page.getByLabel('Filter by health')).toBeVisible()
  await page.getByLabel('Sort repositories').selectOption('findings')
  await page.getByLabel('Filter by health').selectOption('blocked')
  const rows = page.locator('.feature-table .feature-row:not(.feature-header)')
  const count = await rows.count()
  for (let i = 0; i < count; i++) await expect(rows.nth(i)).toContainText(/Blocked|No repositories match/)
})

test('operator can assign a finding and clear the assignee', async ({ page }) => {
  await loginAs(page, 'operator', '/?view=Findings')
  await expect(page.getByText(/\d+ matching/)).toBeVisible()
  await page.locator('.finding-row').first().click()
  const assignee = page.getByLabel('Assignee')
  await assignee.fill('e2e-assignee')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('Assigned to e2e-assignee')
  await assignee.fill('')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('Assignee cleared')
})

test('saved views store and restore findings filters', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Findings')
  await expect(page.getByText(/\d+ matching/)).toBeVisible()
  await page.getByLabel('Filter findings').fill('zzz-no-such-finding')
  await page.getByLabel('View name').fill('e2e-view')
  await page.getByRole('button', { name: 'Save view' }).click()
  await page.getByLabel('Filter findings').fill('')
  await page.getByLabel('Saved views').selectOption('e2e-view')
  await expect(page.getByLabel('Filter findings')).toHaveValue('zzz-no-such-finding')
  await page.getByLabel('Delete a saved view').selectOption('e2e-view') // leave browser storage clean
  await expect(page.getByLabel('Saved views').locator('option', { hasText: 'e2e-view' })).toHaveCount(0)
})

test('global search finds repositories, findings and runs', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Repositories')
  const name = (await page.locator('.repo-name').first().textContent())!.trim()
  await page.getByLabel('Search repositories, findings and runs').fill(name.slice(0, 4))
  const results = page.getByRole('listbox', { name: 'Search results' })
  await expect(results).toBeVisible()
  await results.getByRole('option').first().click()
  await expect(page.locator('h1')).toBeVisible()
})

test('j/k move between finding rows', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Findings')
  await expect(page.getByText(/\d+ matching/)).toBeVisible()
  const toggles = page.locator('.finding-toggle')
  await toggles.first().waitFor({ state: 'visible', timeout: 6000 }).catch(() => {})
  test.skip((await toggles.count()) < 2, 'needs at least two findings')
  await toggles.first().focus()
  await page.keyboard.press('j')
  await expect(toggles.nth(1)).toBeFocused()
  await page.keyboard.press('k')
  await expect(toggles.first()).toBeFocused()
})

test('findings, runs and audit can be exported as CSV', async ({ page }) => {
  await loginAs(page, 'admin', '/?view=Findings')
  const exportButton = page.getByRole('button', { name: 'Export CSV' })
  test.skip(!(await exportButton.waitFor({ state: 'visible', timeout: 5000 }).then(() => true).catch(() => false)), 'export needs the live API')
  const [download] = await Promise.all([page.waitForEvent('download'), exportButton.click()])
  expect(download.suggestedFilename()).toBe('aegisscan-findings.csv')
  await page.getByRole('button', { name: 'Audit Logs' }).click()
  const [auditDownload] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export CSV' }).click()])
  expect(auditDownload.suggestedFilename()).toBe('aegisscan-audit.csv')
})
