import { defineConfig } from '@playwright/test';

/**
 * Chrome 扩展需用 Playwright 自带的 Chromium + launchPersistentContext 加载。
 * https://playwright.dev/docs/chrome-extensions
 *
 * 可选：本机已启动 Office Copilot 后端时设置环境变量，以启用 API / @ 模式相关用例：
 *   set OFFICE_COPILOT_E2E_API_BASE=http://127.0.0.1:8765
 * （若端口被占用请以实际引导接口为准。）
 */
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    trace: 'on-first-retry',
    baseURL: process.env.OFFICE_COPILOT_E2E_API_BASE || 'http://127.0.0.1:8765',
  },
});
