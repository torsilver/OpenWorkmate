import { test, expect, tinyPngBuffer } from '../fixtures';

/**
 * 对应 `docs/Chrome端手工测试计划.md` §1.1 中可稳定自动化的子集。
 * 无法覆盖项见 `docs/Chrome端手工测试-Playwright无法覆盖清单.md`。
 */
test.describe('§1.1 侧栏壳（Playwright）', () => {
  test('C1 侧栏页加载为 Office Copilot 对话壳', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/sidepanel.html`);
    await expect(page).toHaveTitle(/Office Copilot/);
    await expect(page.locator('.welcome-title')).toContainText('Office Copilot');
    await expect(page.locator('#messages')).toBeVisible();
    await expect(page.locator('#input')).toBeVisible();
  });

  test('C3 设置按钮打开 options.html 新标签', async ({ context, page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/sidepanel.html`);
    const opened = context.waitForEvent('page');
    await page.locator('#settings-btn').click();
    const optionsPage = await opened;
    await optionsPage.waitForLoadState('domcontentloaded');
    expect(optionsPage.url()).toContain('options.html');
    await expect(optionsPage.locator('.container')).toBeVisible();
  });

  test('C4 新对话恢复欢迎区并清空附件区', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/sidepanel.html`);
    await page.locator('#input').fill('ping');
    await page.locator('#attach-btn').click();
    await page.locator('#file-input').setInputFiles({
      name: 'one.png',
      mimeType: 'image/png',
      buffer: tinyPngBuffer,
    });
    await expect(page.locator('#attachments-preview')).toBeVisible();
    await expect(page.locator('.attachment-thumb')).toBeVisible();

    await page.locator('#new-chat-btn').click();
    await expect(page.locator('.welcome-title')).toContainText('Office Copilot');
    await expect(page.locator('#attachments-preview')).toBeHidden();
  });

  test('C6 回形针仅接受图片：选图后出现预览缩略图', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/sidepanel.html`);
    await expect(page.locator('#file-input')).toHaveAttribute('accept', 'image/*');
    await page.locator('#attach-btn').click();
    await page.locator('#file-input').setInputFiles({
      name: 'taskly-e2e.png',
      mimeType: 'image/png',
      buffer: tinyPngBuffer,
    });
    await expect(page.locator('#attachments-preview')).toBeVisible();
    await expect(page.locator('.attachment-thumb-wrap')).toHaveCount(1);
  });

  test('输入框占位含 @ 提示（与 §1.4 入口一致）', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/sidepanel.html`);
    await expect(page.locator('#input')).toHaveAttribute('placeholder', /@/);
  });
});

test.describe('选项页直达', () => {
  test('options.html 标题与主容器', async ({ page, extensionId }) => {
    await page.goto(`chrome-extension://${extensionId}/options.html`);
    await expect(page).toHaveTitle(/Office Copilot/);
    await expect(page.locator('.container')).toBeVisible();
  });
});
