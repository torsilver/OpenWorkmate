import { test, expect } from '../fixtures';
import { createAuthedApiContext } from '../api-auth';

async function tryGetOk(
  request: { get: (url: string) => Promise<{ ok: () => boolean }> },
  path: string,
): Promise<boolean> {
  try {
    const r = await request.get(path);
    return r.ok();
  } catch {
    return false;
  }
}

/**
 * 需本机已启动后端，且 `OFFICE_COPILOT_E2E_API_BASE`（或默认 127.0.0.1:8765）可访问。
 * 连不上则 skip，不判失败（避免无后端时 CI/本地误红）。
 */
test.describe('后端可选：HTTP 与 §1.4 A1 壳', () => {
  test('Z1/HTTP：引导或 tools/builtin 至少其一可访问', async ({ request }) => {
    const bootOk = await tryGetOk(request, '/api/bootstrap/local-service-auth');
    const toolsOk = await tryGetOk(request, '/api/tools/builtin');
    if (!bootOk && !toolsOk) {
      test.skip();
    }
    expect(bootOk || toolsOk).toBeTruthy();
  });

  test('GET /api/tools/builtin 返回非空插件数组', async ({ request }) => {
    if (!(await tryGetOk(request, '/api/bootstrap/local-service-auth'))) {
      if (!(await tryGetOk(request, '/api/tools/builtin'))) test.skip();
    }
    const api = await createAuthedApiContext();
    try {
      const res = await api.get('/api/tools/builtin');
      if (!res.ok()) test.skip();
      const data = await res.json();
      expect(Array.isArray(data)).toBeTruthy();
      expect(data.length).toBeGreaterThanOrEqual(18);
    } finally {
      await api.dispose();
    }
  });

  test('A1 输入 @ 后出现工具/技能列表项（需后端与接口成功）', async ({
    page,
    extensionId,
    request,
  }) => {
    const api = await createAuthedApiContext();
    try {
      if (!(await tryGetOk(api, '/api/tools/builtin'))) {
        test.skip();
      }
    } finally {
      await api.dispose();
    }
    await page.goto(`chrome-extension://${extensionId}/sidepanel.html`);
    const input = page.locator('#input');
    await input.click();
    await input.type('@', { delay: 50 });
    await expect(page.locator('#at-mode-panel')).toBeVisible({ timeout: 15000 });
    await expect(page.locator('.at-mode-item').first()).toBeVisible({ timeout: 15000 });
  });
});
