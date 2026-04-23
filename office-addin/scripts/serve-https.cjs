'use strict';

/**
 * 本地 HTTPS 静态托管 office-addin（端口默认 3000，与 manifest.xml 中 URL 一致）。
 * 证书由 office-addin-dev-certs 生成并确保已安装到本机信任库。
 */

const https = require('https');
const fs = require('fs');
const path = require('path');
const devCerts = require('office-addin-dev-certs');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number.parseInt(process.env.PORT || '3000', 10);

function contentType(filePath) {
  const lower = filePath.toLowerCase();
  if (lower.endsWith('.html')) return 'text/html; charset=utf-8';
  if (lower.endsWith('.js')) return 'text/javascript; charset=utf-8';
  if (lower.endsWith('.css')) return 'text/css; charset=utf-8';
  if (lower.endsWith('.json')) return 'application/json; charset=utf-8';
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.jpg') || lower.endsWith('.jpeg')) return 'image/jpeg';
  if (lower.endsWith('.svg')) return 'image/svg+xml';
  if (lower.endsWith('.woff2')) return 'font/woff2';
  if (lower.endsWith('.ttf')) return 'font/ttf';
  if (lower.endsWith('.txt')) return 'text/plain; charset=utf-8';
  if (lower.endsWith('.xml')) return 'application/xml; charset=utf-8';
  return 'application/octet-stream';
}

function safeResolve(urlPath) {
  let rel = decodeURIComponent(urlPath.split('?')[0]);
  if (rel.includes('\0')) return null;
  rel = rel.replace(/^\/+/, '');
  if (!rel) rel = 'taskpane.html';
  const fsPath = path.resolve(ROOT, rel);
  const normalizedRoot = path.resolve(ROOT);
  if (!fsPath.startsWith(normalizedRoot)) return null;
  return fsPath;
}

(async () => {
  const options = await devCerts.getHttpsServerOptions(undefined, undefined, false);

  const server = https.createServer(options, (req, res) => {
    try {
      const rawPath = req.url || '/';
      let fsPath = safeResolve(rawPath === '/' ? '/taskpane.html' : rawPath);

      if (!fsPath || !fs.existsSync(fsPath)) {
        res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('Not found');
        return;
      }

      let st = fs.statSync(fsPath);
      if (st.isDirectory()) {
        const idx = path.join(fsPath, 'taskpane.html');
        if (fs.existsSync(idx)) {
          fsPath = idx;
          st = fs.statSync(fsPath);
        } else {
          res.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' });
          res.end('Directory listing disabled');
          return;
        }
      }

      res.setHeader('Content-Type', contentType(fsPath));
      fs.createReadStream(fsPath).pipe(res);
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end(e && e.message ? e.message : String(e));
    }
  });

  server.listen(PORT, () => {
    /* eslint-disable no-console */
    console.log('');
    console.log('[office-addin] HTTPS 已就绪（信任证书由 office-addin-dev-certs 维护）');
    console.log(`  目录: ${ROOT}`);
    console.log(`  任务窗格: https://localhost:${PORT}/taskpane.html`);
    console.log(`  清单:     https://localhost:${PORT}/manifest.xml`);
    console.log('');
    console.log('—— Word（优先）旁加载 ——');
    console.log('  1) 先启动本机后端（默认 http://localhost:8765），并在 Chrome 扩展里配好 AI。');
    console.log('  2) 打开 Word →「插入」→「加载项」→「我的加载项」→「上传我的加载项」。');
    console.log('  3) 选择本目录下的 manifest.xml（或填入上述 manifest HTTPS 地址，视 Office 版本界面而定）。');
    console.log('  备选：「文件」→「选项」→「信任中心」→「信任中心设置」→「受信任的加载项目录」。');
    console.log('');
    console.log('图标：manifest 指向 /assets/icon-32.png、icon-64.png；若缺失可能导致图标不显示，不影响加载项调试。');
    console.log('按 Ctrl+C 停止服务。');
    console.log('');
    /* eslint-enable no-console */
  });
})().catch((err) => {
  console.error('[office-addin] 启动失败:', err.message || err);
  process.exit(1);
});
