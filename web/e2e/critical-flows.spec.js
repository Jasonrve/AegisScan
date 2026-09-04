import { expect, test } from '@playwright/test'

test('viewer can inspect but cannot launch scans', async ({ page }) => {
  await page.goto('/?role=viewer')
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Run Scan' })).toHaveCount(0)
})

test('operator can launch a confirmed full scan', async ({ page }) => {
  await page.goto('/?role=operator')
  await page.getByRole('button', { name: 'Run Scan' }).click()
  await page.getByRole('combobox', { name: 'Scan mode' }).selectOption({ label: 'Full' })
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: /Queue scan/ }).click()
  await expect(page.getByRole('status')).toContainText('Scan queued')
})

test('scan dialog closes with Escape', async ({ page }) => {
  await page.goto('/?role=operator')
  await page.getByRole('button', { name: 'Run Scan' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
})
