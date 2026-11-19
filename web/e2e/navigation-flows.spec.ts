import { expect, test } from '@playwright/test'
import { loginAs } from './login'

test('repository creation and live dashboard counts work', async ({ page }) => {
  await loginAs(page, 'admin')
  const repositoryName = `team/edge-service-${Date.now()}`
  await page.getByRole('button', { name: 'Repositories' }).click()
  await expect(page.getByRole('heading', { name: 'Managed repositories' })).toBeVisible()
  await page.getByRole('button', { name: /Add repository/ }).click()
  await page.getByLabel('Repository name').fill(repositoryName)
  await page.getByLabel('Clone URL').fill(`https://github.com/${repositoryName}.git`)
  await page.getByRole('button', { name: /Add repository/ }).last().click()
  await expect(page.getByRole('status')).toContainText('Repository added')
  await page.getByRole('button', { name: 'Repositories' }).click()
  // The list is paged and sorted, so find the new repository the way a user would: by filtering.
  await page.getByLabel('Filter repositories').fill(repositoryName)
  await expect(page.getByText(repositoryName)).toBeVisible()
  // Clean up so shared compose data does not accumulate test repositories.
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByRole('status')).toContainText('Repository removed')
})

test('primary and administration navigation opens functional views', async ({ page }) => {
  await loginAs(page, 'admin')
  for (const view of ['Findings', 'Alerts', 'Policies', 'Settings', 'Roles', 'Audit Logs', 'System']) {
    await page.getByRole('button', { name: view, exact: true }).click()
    await expect(page.locator('h1')).toHaveText(view)
    await expect(page.locator('.feature-view, .empty-view')).toBeVisible()
  }
})

test('settings save and queued run cancellation update live state', async ({ page }) => {
  await loginAs(page, 'admin')
  await page.getByRole('button', { name: 'Settings', exact: true }).click()
  await page.getByLabel('Max concurrency').fill('7')
  await page.getByRole('button', { name: /Save changes/ }).click()
  await expect(page.getByRole('status')).toContainText('Settings saved')
  await page.getByRole('button', { name: /^Scans/ }).click()
  await page.getByRole('button', { name: 'Run Scan', exact: true }).click()
  await page.getByRole('button', { name: /Queue scan/ }).click()
  await page.getByRole('button', { name: /^Scans/ }).click()
  await page.getByRole('button', { name: 'Cancel' }).first().click()
  await expect(page.locator('.pill.canceled').first()).toBeVisible()
})
