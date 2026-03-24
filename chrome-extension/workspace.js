const TASKLY_API_BASE = "http://localhost:8765";

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

fetch(TASKLY_API_BASE + "/api/config")
  .then((r) => (r.ok ? r.json() : null))
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