import { test, expect } from '../fixtures';

/**
 * 扩展内静态/辅助页可达性（对应手工文档 §1.7 / §五 D1 等「页面能打开」层面）。
 */
test.describe('扩展内辅助页', () => {
  test('workspace.html 可加载', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/workspace.html`);
    await expect(page.locator('body')).toBeVisible();
  });

  test('plans.html 可加载（带占位 id）', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/plans.html?id=e2e-smoke`);
    await expect(page.locator('body')).toBeVisible();
  });

  test('meeting-live.html 可加载', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/meeting-live.html`);
    await expect(page.locator('body')).toBeVisible();
  });

  test('D1 debug-stats.html 可加载且无致命空白', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/debug-stats.html`);
    await expect(page.locator('body')).toBeVisible();
    const text = await page.locator('body').innerText();
    expect(text.length).toBeGreaterThan(10);
  });
});
