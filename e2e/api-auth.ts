import { request, type APIRequestContext } from '@playwright/test';

/** 与 playwright.config.ts `use.baseURL` 一致 */
export function apiBaseURL(): string {
  return process.env.OPEN_WORKMATE_E2E_API_BASE || 'http://127.0.0.1:8765';
}

/**
 * 先拉 loopback 的 bootstrap 拿到 `webSocketAuthToken`，再带 `X-OpenWorkmate-Token` 调其它 /api/*。
 * 与服务端 `LocalApiAuthMiddleware` 行为一致。
 */
export async function createAuthedApiContext(): Promise<APIRequestContext> {
  const baseURL = apiBaseURL();
  const anon = await request.newContext({ baseURL });
  let headers: Record<string, string> = {};
  try {
    const boot = await anon.get('/api/bootstrap/local-service-auth');
    if (boot.ok()) {
      try {
        const j = (await boot.json()) as { webSocketAuthToken?: string };
        const t = (j.webSocketAuthToken && String(j.webSocketAuthToken).trim()) || '';
        if (t) headers['X-OpenWorkmate-Token'] = t;
      } catch {
        /* ignore body parse */
      }
    }
  } finally {
    await anon.dispose();
  }
  return request.newContext({ baseURL, extraHTTPHeaders: headers });
}
