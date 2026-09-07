import { test, expect } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { loginAs } from './login'

const views = ['Dashboard', 'Repositories', 'Scans', 'Findings', 'Settings', 'Policies']

for (const view of views) {
  test(`${view} has no critical accessibility violations`, async ({ page }) => {
    await loginAs(page, 'admin')
    if (view !== 'Dashboard') await page.getByRole('button', { name: new RegExp(`^${view}`) }).click()
    const results = await new AxeBuilder({ page }).analyze()
    const critical = results.violations.filter(violation => violation.impact === 'critical')
    expect(critical, critical.map(violation => `${violation.id}: ${violation.help}`).join('\n')).toEqual([])
  })
}
