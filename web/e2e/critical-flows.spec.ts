import { expect, test } from '@playwright/test'
import { loginAs } from './login'

test('viewer can inspect but cannot launch scans', async ({ page }) => {
  await loginAs(page, 'viewer')
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  await expect(page.getByRole('button', { name: /Run Scan/ })).toBeDisabled()
  await expect(page.getByRole('button', { name: /Run Scan/ })).toHaveAttribute('title', 'Requires the operator role')
})

test('operator can launch a confirmed full scan', async ({ page }) => {
  await loginAs(page, 'operator')
  await page.getByRole('button', { name: 'Run Scan' }).click()
  await page.getByRole('combobox', { name: 'Scan mode' }).selectOption({ label: 'Full' })
  await page.getByRole('button', { name: /Queue scan/ }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Continue' }).click()
  await expect(page.getByRole('status')).toContainText('Scan queued')
})

test('scan dialog closes with Escape', async ({ page }) => {
  await loginAs(page, 'operator')
  await page.getByRole('button', { name: 'Run Scan' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
})
