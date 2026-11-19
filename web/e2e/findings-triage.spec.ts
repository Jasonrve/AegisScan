import { expect, test } from '@playwright/test'
import { loginAs } from './login'

// Expand a finding, ignore it, find it under the "Ignored" filter, then reopen it (leaves shared compose data unchanged).
test('operator can triage a finding: ignore then reopen', async ({ page }) => {
  await loginAs(page, 'operator', '/?view=Findings')
  await expect(page.getByText(/\d+ matching/)).toBeVisible()
  const firstRow = page.locator('.finding-row').first()
  await firstRow.click()
  const fingerprint = (await page.locator('.finding-details .fingerprint code').textContent())!.trim()
  await page.getByRole('button', { name: 'Ignore', exact: true }).click()
  await page.getByLabel('Reason').fill('e2e: accepted risk')
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(page.getByRole('status')).toContainText('Finding updated')

  // Narrow to exactly this finding (shared compose data holds many) and look for it under "Ignored".
  await page.getByLabel('Filter findings').fill(fingerprint)
  await page.getByLabel('Filter by state').selectOption('ignored')
  await expect(page.locator('.finding-row')).toHaveCount(1)
  if (!(await page.locator('.finding-details').isVisible())) await page.locator('.finding-row').click()
  await expect(page.locator('.finding-details .fingerprint code')).toHaveText(fingerprint)
  await page.getByRole('button', { name: 'Reopen' }).click()
  await expect(page.getByRole('status')).toContainText('Finding updated')
})

test('viewer cannot triage findings', async ({ page }) => {
  await loginAs(page, 'viewer', '/?view=Findings')
  await page.locator('.finding-row').first().click()
  await expect(page.getByRole('button', { name: 'Ignore', exact: true })).toHaveCount(0)
})

// Bulk bar: tick one row, ignore it in bulk, find it under "Ignored" (scoped by fingerprint), reopen it in bulk.
test('operator can bulk triage findings', async ({ page }) => {
  await loginAs(page, 'operator', '/?view=Findings')
  await expect(page.getByText(/\d+ matching/)).toBeVisible()
  await page.locator('.finding-row').first().click()
  const fingerprint = (await page.locator('.finding-details .fingerprint code').textContent())!.trim()
  await page.locator('.finding-row').first().locator('input[type=checkbox]').check()
  const bar = page.getByRole('region', { name: 'Bulk triage' })
  await expect(bar).toContainText('1 selected')
  await bar.getByRole('button', { name: 'Ignore', exact: true }).click()
  await bar.getByLabel('Reason').fill('e2e: bulk accepted risk')
  await bar.getByRole('button', { name: 'Confirm' }).click()
  await expect(page.getByRole('status')).toContainText('1 finding updated')

  await page.getByLabel('Filter findings').fill(fingerprint)
  await page.getByLabel('Filter by state').selectOption('ignored')
  await expect(page.locator('.finding-row')).toHaveCount(1)
  await page.getByLabel('Select all findings on this page').check()
  await page.getByRole('region', { name: 'Bulk triage' }).getByRole('button', { name: 'Reopen' }).click()
  await expect(page.getByRole('status')).toContainText('1 finding updated')
})
