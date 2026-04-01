let TASKLY_API_BASE = "http://127.0.0.1:8765";
const COPILOT_TOKEN_STORAGE_KEY = "localServiceAuthToken";

var tasklyWorkspaceApiReady = null;
function tasklyEnsureWorkspaceApiBase() {
  if (tasklyWorkspaceApiReady) return tasklyWorkspaceApiReady;
  tasklyWorkspaceApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(
    typeof chrome !== "undefined" && chrome.storage && chrome.storage.local ? chrome.storage.local : null
  )
    .then(function (r) {
      TASKLY_API_BASE = TasklyLocalService.normalizeBase(r.baseUrl);
    })
    .catch(function (err) {
      // 自行缓存的 Promise：失败时必须清空，否则后台晚启动后仍会命中旧的 rejected
      tasklyWorkspaceApiReady = null;
      throw err;
    });
  return tasklyWorkspaceApiReady;
}

function tasklyFetch(url, init) {
  init = init ? Object.assign({}, init) : {};
  return new Promise(function (resolve) {
    chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (r) {
      var t = (r && r[COPILOT_TOKEN_STORAGE_KEY] || "").trim();
      var headers = Object.assign({}, init.headers || {});
      if (t) headers["X-OfficeCopilot-Token"] = t;
      init.headers = headers;
      resolve(fetch(url, init));
    });
  });
}

function ensureLocalServiceTokenFromBootstrap() {
  return tasklyEnsureWorkspaceApiBase().then(function () {
  return fetch(TASKLY_API_BASE + "/api/bootstrap/local-service-auth")
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (j) {
      if (!j || !j.ok) return;
      var t = (j.webSocketAuthToken || "").trim();
      if (!t) return;
      return new Promise(function (resolve) {
        chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (cur) {
          var existing = (cur && cur[COPILOT_TOKEN_STORAGE_KEY] || "").trim();
          if (existing) { resolve(); return; }
          var o = {};
          o[COPILOT_TOKEN_STORAGE_KEY] = t;
          chrome.storage.local.set(o, function () { resolve(); });
        });
      });
    })
    .catch(function () {});
  });
}

function tasklyRefreshEmbedThemes() {
  const t = document.documentElement.getAttribute("data-theme") || "dark";
  const link = document.getElementById("taskly-hljs-theme");
  if (link && typeof TasklyTheme !== "undefined") {
    link.href = TasklyTheme.getHljsStylesheetHref(t);
  }
  if (typeof mermaid !== "undefined" && typeof TasklyTheme !== "undefined") {
    mermaid.initialize({ startOnLoad: false, theme: TasklyTheme.getMermaidTheme(t) });
  }
}

window.addEventListener("storage", (e) => {
  if (e.key !== "tasklyUiTheme") return;
  if (typeof TasklyTheme !== "undefined") {
    TasklyTheme.applyThemeDomOnly(e.newValue != null && e.newValue !== "" ? e.newValue : "dark");
  }
  tasklyRefreshEmbedThemes();
});

ensureLocalServiceTokenFromBootstrap().then(function () {
  return tasklyFetch(TASKLY_API_BASE + "/api/config");
})
  .then((r) => (r && r.ok ? r.json() : null))
  .then((j) => {
    if (!j || typeof TasklyTheme === "undefined") return;
    const id = j.uiThemeId || j.UiThemeId;
    if (id) TasklyTheme.setTheme(id);
    tasklyRefreshEmbedThemes();
  })
  .catch(() => {});

// Initialize libraries
if (typeof marked !== 'undefined') {
  marked.setOptions({
    highlight: function(code, lang) {
      if (lang && hljs.getLanguage(lang)) {
        return hljs.highlight(code, { language: lang }).value;
      }
      return hljs.highlightAuto(code).value;
    },
    breaks: true
  });
}

tasklyRefreshEmbedThemes();

const $emptyState = document.getElementById('empty-state');
const $markdownContainer = document.getElementById('markdown-container');
const $canvasFrame = document.getElementById('canvas-frame');
const $statusText = document.getElementById('status-text');

chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
  if (request.type === 'RENDER_WORKSPACE') {
    $emptyState.style.display = 'none';
    $statusText.textContent = '已更新';
    
    if (request.htmlCode) {
      // Render HTML Canvas
      $markdownContainer.style.display = 'none';
      $canvasFrame.style.display = 'block';
      $canvasFrame.srcdoc = request.htmlCode;
    } else if (request.markdown) {
      // Render Markdown
      $canvasFrame.style.display = 'none';
      $markdownContainer.style.display = 'block';
      $markdownContainer.innerHTML = marked.parse(request.markdown);
      
      // Render mermaid
      if (typeof mermaid !== 'undefined') {
        const mermaidBlocks = $markdownContainer.querySelectorAll('.language-mermaid');
        mermaidBlocks.forEach((block, index) => {
          const id = `mermaid-ws-${Date.now()}-${index}`;
          const code = block.textContent;
          const container = document.createElement('div');
          container.className = 'mermaid-container';
          container.id = id;
          block.parentNode.replaceWith(container);
          
          mermaid.render(id + '-svg', code).then(result => {
            container.innerHTML = result.svg;
          }).catch(err => {
            container.innerHTML = `<pre>Mermaid Error: ${err.message}</pre>`;
          });
        });
      }
    }
    sendResponse({ success: true });
  }
});

// Notify that workspace is ready
chrome.runtime.sendMessage({ type: 'WORKSPACE_READY' });