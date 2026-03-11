const WS_URL = "ws://localhost:8765/ws";
const AUTH_TOKEN = "office-copilot-dev-token";

const RECONNECT_BASE_MS = 1000;
const RECONNECT_MAX_MS = 16000;

const $messages = document.getElementById("messages");
const $input = document.getElementById("input");
const $sendBtn = document.getElementById("send-btn");
const $stopBtn = document.getElementById("stop-btn");
const $attachBtn = document.getElementById("attach-btn");
const $fileInput = document.getElementById("file-input");
const $attachmentsPreview = document.getElementById("attachments-preview");
const $status = document.getElementById("status");
const $settingsBtn = document.getElementById("settings-btn");

if ($settingsBtn) {
  $settingsBtn.addEventListener("click", () => {
    chrome.runtime.openOptionsPage();
  });
}

let ws = null;
let sessionId = null;
let reconnectDelay = RECONNECT_BASE_MS;
let reconnectTimer = null;
let streamingBubble = null;
let currentMode = "workspace"; // 'workspace' or 'assistant'
const attachments = []; // { mimeType, data (base64), id } for preview

const $modeSwitch = document.getElementById("mode-switch");
if ($modeSwitch) {
  $modeSwitch.addEventListener("change", (e) => {
    currentMode = e.target.checked ? "assistant" : "workspace";
    addSystemMessage(`已切换至 ${currentMode === 'assistant' ? '辅助 (Assistant)' : '工作区 (Workspace)'} 模式`);
    // TODO: Notify backend about mode change
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: "mode_change", content: currentMode }));
    }
  });
}

// ───── Session ID ─────

function getSessionId() {
  let id = sessionStorage.getItem("copilot_session_id");
  if (!id) {
    id = crypto.randomUUID().replace(/-/g, "").slice(0, 12);
    sessionStorage.setItem("copilot_session_id", id);
  }
  return id;
}

// ───── Debug Panel & Runtime Log ─────
const DEBUG_LOG_MAX = 300;
const debugLogBuffer = [];
const $debugPanel = document.getElementById("debug-panel");
const $toggleDebugBtn = document.getElementById("toggle-debug-btn");
const $closeDebugBtn = document.getElementById("close-debug-btn");
const $debugContent = document.getElementById("debug-content");
const $debugRuntimeLog = document.getElementById("debug-runtime-log");
const $clearRuntimeLogBtn = document.getElementById("clear-runtime-log-btn");

function debugLog(tag, message, type = "info") {
  const ts = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  const line = `[${ts}] [${tag}] ${message}`;
  console.log("[OfficeCopilot]", tag, message);
  debugLogBuffer.push({ ts, tag, message, type });
  if (debugLogBuffer.length > DEBUG_LOG_MAX) debugLogBuffer.shift();
  if ($debugRuntimeLog) {
    const div = document.createElement("div");
    div.className = "log-line log--" + (type === "send" ? "send" : type === "recv" ? "recv" : type === "rpc" ? "rpc" : type === "err" ? "err" : "");
    div.textContent = line;
    $debugRuntimeLog.appendChild(div);
    $debugRuntimeLog.scrollTop = $debugRuntimeLog.scrollHeight;
  }
}

if ($toggleDebugBtn) {
  $toggleDebugBtn.addEventListener("click", () => {
    const isHidden = $debugPanel.style.display === "none";
    $debugPanel.style.display = isHidden ? "flex" : "none";
    if (isHidden) {
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: "get_debug_history" }));
      }
    }
  });
}
if ($closeDebugBtn) {
  $closeDebugBtn.addEventListener("click", () => {
    $debugPanel.style.display = "none";
  });
}
document.querySelectorAll(".debug-tab").forEach((btn) => {
  btn.addEventListener("click", () => {
    const tab = btn.dataset.tab;
    document.querySelectorAll(".debug-tab").forEach((b) => b.classList.remove("active"));
    btn.classList.add("active");
    if (tab === "history") {
      if ($debugContent) $debugContent.style.display = "block";
      if ($debugRuntimeLog) $debugRuntimeLog.style.display = "none";
    } else {
      if ($debugContent) $debugContent.style.display = "none";
      if ($debugRuntimeLog) $debugRuntimeLog.style.display = "block";
    }
  });
});
if ($clearRuntimeLogBtn) {
  $clearRuntimeLogBtn.addEventListener("click", () => {
    debugLogBuffer.length = 0;
    if ($debugRuntimeLog) $debugRuntimeLog.innerHTML = "";
    debugLog("Log", "运行日志已清空");
  });
}

function addDebugLog(role, content) {
  if (!$debugContent) return;
  const div = document.createElement("div");
  div.className = "debug-item";
  div.innerHTML = `<span class="debug-item-role">[${role}]</span><br/>${escapeHtml(content)}`;
  $debugContent.appendChild(div);
  $debugContent.scrollTop = $debugContent.scrollHeight;
}

function escapeHtml(unsafe) {
    return unsafe
         .replace(/&/g, "&amp;")
         .replace(/</g, "&lt;")
         .replace(/>/g, "&gt;")
         .replace(/"/g, "&quot;")
         .replace(/'/g, "&#039;");
}

// ───── WebSocket ─────

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
    return;
  }

  sessionId = getSessionId();
  const url = `${WS_URL}?sessionId=${sessionId}&token=${AUTH_TOKEN}`;
  ws = new WebSocket(url);

  ws.addEventListener("open", () => {
    reconnectDelay = RECONNECT_BASE_MS;
    setStatus(true);
    addSystemMessage("已连接到本地服务");
    debugLog("WS", "connected sessionId=" + sessionId, "recv");
    ws.send(JSON.stringify({ type: "mode_change", content: currentMode }));
  });

  ws.addEventListener("message", (e) => {
    handleMessage(e.data);
  });

  ws.addEventListener("close", () => {
    setStatus(false);
    finalizeStream();
    debugLog("WS", "closed", "recv");
    scheduleReconnect();
  });

  ws.addEventListener("error", () => {
    debugLog("WS", "error", "err");
    ws.close();
  });
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
    connect();
  }, reconnectDelay);
}

function send(text, attachmentsPayload = null) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  const payload = attachmentsPayload && attachmentsPayload.length > 0
    ? JSON.stringify({ type: "text", content: text || "", attachments: attachmentsPayload })
    : JSON.stringify({ type: "text", content: text });
  ws.send(payload);
  debugLog("WS Send", "type=text len=" + (text || "").length + " attachments=" + (attachmentsPayload?.length || 0), "send");
}

// ───── Init Libraries ─────
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

if (typeof mermaid !== 'undefined') {
  mermaid.initialize({ startOnLoad: false, theme: 'dark' });
}

// ───── Streaming state ─────

let currentBotMessageRaw = "";
// 本轮回复的容器与可折叠「执行过程」区域（多层折叠：执行过程 → 每个工具块）
let currentRoundWrapper = null;
let executionLogSection = null;   // <details> 外层
let executionLogBody = null;       // 内层 div，挂多个工具块
let executionLogSummaryEl = null;  // 用于更新「执行过程 (N 个操作)」
let currentRoundToolBlocks = [];   // 本轮的每个工具块 <details>
let currentToolEndIndex = 0;

function beginStream() {
  const welcome = $messages.querySelector(".welcome");
  if (welcome) welcome.remove();

  currentRoundWrapper = document.createElement("div");
  currentRoundWrapper.className = "msg msg--round";

  streamingBubble = document.createElement("div");
  streamingBubble.className = "msg msg--bot msg--streaming";
  streamingBubble.textContent = "";
  currentRoundWrapper.appendChild(streamingBubble);

  // 执行过程：进行中时展开，最终答案给出后再折叠
  executionLogSection = document.createElement("details");
  executionLogSection.className = "msg msg--execution-log";
  executionLogSummaryEl = document.createElement("summary");
  executionLogSummaryEl.textContent = "执行过程 (0 个操作)";
  executionLogSection.appendChild(executionLogSummaryEl);
  executionLogBody = document.createElement("div");
  executionLogBody.className = "execution-log-body";
  executionLogSection.appendChild(executionLogBody);
  executionLogSection.open = false; // 有块时在 updateExecutionLogCount 里会打开
  currentRoundWrapper.appendChild(executionLogSection);

  $messages.appendChild(currentRoundWrapper);
  currentBotMessageRaw = "";
  currentRoundToolBlocks = [];
  currentToolEndIndex = 0;
  setInputEnabled(false);
}

function updateExecutionLogCount() {
  if (executionLogSummaryEl) executionLogSummaryEl.textContent = "执行过程 (" + currentRoundToolBlocks.length + " 个操作)";
  // 进行中：有新的执行过程时保持展开
  if (executionLogSection && currentRoundToolBlocks.length > 0) executionLogSection.open = true;
}

function appendStreamChunk(text) {
  if (!streamingBubble) {
    beginStream();
  }
  currentBotMessageRaw += text;
  
  // Try to render markdown during streaming, but it might be incomplete
  if (typeof marked !== 'undefined') {
    streamingBubble.innerHTML = marked.parse(currentBotMessageRaw);
  } else {
    streamingBubble.textContent = currentBotMessageRaw;
  }
  
  $messages.scrollTop = $messages.scrollHeight;
}

function finalizeStream() {
  if (streamingBubble) {
    streamingBubble.classList.remove("msg--streaming");
    if (typeof marked !== 'undefined') {
      streamingBubble.innerHTML = marked.parse(currentBotMessageRaw);
      if (typeof mermaid !== 'undefined') {
        const mermaidBlocks = streamingBubble.querySelectorAll('.language-mermaid');
        mermaidBlocks.forEach((block, index) => {
          const id = `mermaid-${Date.now()}-${index}`;
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
    streamingBubble = null;
    currentBotMessageRaw = "";
  }
  if (executionLogSection) {
    executionLogSection.style.display = currentRoundToolBlocks.length === 0 ? "none" : "";
    if (currentRoundToolBlocks.length > 0) {
      executionLogSection.open = false; // 给出最终答案后折叠「执行过程」
      currentRoundToolBlocks.forEach(function (b) { b.open = false; });
    }
  }
  currentRoundWrapper = null;
  executionLogSection = null;
  executionLogBody = null;
  executionLogSummaryEl = null;
  currentRoundToolBlocks = [];
  currentToolEndIndex = 0;
  setInputEnabled(true);
}

// ───── Message handling ─────

function handleMessage(raw) {
  let msg;
  try {
    msg = JSON.parse(raw);
  } catch {
    msg = { type: "text", content: raw };
  }
  debugLog("WS Recv", "type=" + (msg.type || "?") + (msg.content ? " len=" + (typeof msg.content === "string" ? msg.content.length : 0) : ""), "recv");

  switch (msg.type) {
    case "stream_start":
      beginStream();
      break;

    case "stream_chunk":
      appendStreamChunk(msg.content);
      break;

    case "stream_end":
      const rawMsg = currentBotMessageRaw;
      finalizeStream();
      extractAndRenderCanvas(rawMsg);
      break;

    case "tool_invocation_start": {
      if (!executionLogBody) break;
      const label = msg.summary || `正在执行: ${msg.plugin || ""}.${msg.function || ""}`;
      const block = document.createElement("details");
      block.className = "tool-call-block tool-call--running";
      block.dataset.label = label;
      const sum = document.createElement("summary");
      sum.innerHTML = `<span class="tool-status-icon">⏳</span> ${escapeHtml(label)}`;
      block.appendChild(sum);
      const out = document.createElement("pre");
      out.className = "tool-call-output";
      block.appendChild(out);
      executionLogBody.appendChild(block);
      currentRoundToolBlocks.push(block);
      block.open = true; // 进行中时每个工具块默认展开
      updateExecutionLogCount();
      break;
    }

    case "tool_invocation_end": {
      const block = currentRoundToolBlocks[currentToolEndIndex];
      if (block) {
        const ok = msg.success === true;
        const name = `${msg.plugin || ""}.${msg.function || ""}`;
        const content = (msg.content && String(msg.content).trim()) || "";
        const displayLabel = (block.dataset.label || name).replace(/^正在执行:\s*/i, "");
        block.classList.remove("tool-call--running");
        block.classList.add(ok ? "tool-call--done" : "tool-call--fail");
        const sum = block.querySelector("summary");
        if (sum) sum.innerHTML = `<span class="tool-status-icon">${ok ? "✓" : "✗"}</span> ${escapeHtml(displayLabel)}`;
        const out = block.querySelector(".tool-call-output");
        if (out) {
          out.textContent = content || "";
          out.style.display = content ? "block" : "none";
        }
      }
      currentToolEndIndex++;
      break;
    }

    case "echo":
    case "text":
      addBotMessage(msg.content);
      break;

    case "pong":
      break;

    case "error":
      finalizeStream();
      addBotMessage(msg.content || "请求失败", true);
      break;

    case "debug_history":
      if ($debugContent) {
        $debugContent.innerHTML = "";
        addDebugLog("System/History", msg.content);
      }
      break;

    case "rpc_request":
      handleRpcRequest(msg);
      break;

    case "confirm_request":
      handleConfirmRequest(msg);
      break;

    default:
      addBotMessage(msg.content || JSON.stringify(msg));
  }
}

// ───── RPC Handling ─────
async function handleRpcRequest(msg) {
  const { id, method, params } = msg;
  if (!id || !method) return;
  debugLog("RPC", "req id=" + id + " method=" + method, "rpc");

  try {
    let result = null;
    if (method === "highlight_text") {
      result = await executeInActiveTab(highlightTextInPage, params.text, params.color);
    } else if (method === "add_floating_note") {
      result = await executeInActiveTab(addFloatingNoteInPage, params.message, params.title, params.anchorText);
    } else if (method === "run_page_script") {
      const scriptId = params?.scriptId;
      if (!scriptId || typeof PAGE_SCRIPTS[scriptId] !== "function") {
        throw new Error("未知脚本 ID: " + (scriptId || ""));
      }
      let paramsObj = params?.scriptParams ?? params?.params;
      if (typeof paramsObj === "string") {
        try { paramsObj = paramsObj ? JSON.parse(paramsObj) : {}; } catch (_) { paramsObj = {}; }
      }
      result = await executeInActiveTab(PAGE_SCRIPTS[scriptId], paramsObj || {});
    } else if (method === "capture_full_page") {
      result = await captureFullPage();
    } else {
      throw new Error(`Unknown RPC method: ${method}`);
    }
    debugLog("RPC", "res id=" + id + " ok=" + (result != null), "rpc");
    sendRpcResponse(id, result, null);
  } catch (err) {
    console.error("RPC Error:", err);
    debugLog("RPC", "err id=" + id + " " + err.message, "err");
    sendRpcResponse(id, null, err.message);
  }
}

function sendRpcResponse(id, result, error) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  const payload = JSON.stringify({ type: "rpc_response", id, result, error });
  ws.send(payload);
  debugLog("WS Send", "type=rpc_response id=" + id + (error ? " error=1" : ""), "send");
}

// ───── HITL (危险操作人机确认) ─────
let pendingConfirmId = null;
const $hitlOverlay = document.getElementById("hitl-overlay");
const $hitlAction = document.getElementById("hitl-action");
const $hitlAllowBtn = document.getElementById("hitl-allow-btn");
const $hitlDenyBtn = document.getElementById("hitl-deny-btn");

function handleConfirmRequest(msg) {
  const requestId = msg.id || msg.requestId;
  const action = msg.content || msg.action || "未知操作";
  if (!requestId) {
    debugLog("HITL", "confirm_request missing id", "err");
    return;
  }
  pendingConfirmId = requestId;
  if ($hitlAction) $hitlAction.textContent = action;
  if ($hitlOverlay) {
    $hitlOverlay.style.display = "flex";
    $hitlOverlay.setAttribute("aria-hidden", "false");
  }
  debugLog("HITL", "confirm_request id=" + requestId + " action=" + action.slice(0, 50), "recv");
}

function sendConfirmResponse(id, allowed) {
  if (!id) return;
  if (ws && ws.readyState === WebSocket.OPEN) {
    const payload = JSON.stringify({ type: "confirm_response", id, allowed });
    ws.send(payload);
    debugLog("WS Send", "type=confirm_response id=" + id + " allowed=" + allowed, "send");
  }
  pendingConfirmId = null;
  if ($hitlOverlay) {
    $hitlOverlay.style.display = "none";
    $hitlOverlay.setAttribute("aria-hidden", "true");
  }
}

if ($hitlAllowBtn) {
  $hitlAllowBtn.addEventListener("click", () => {
    if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true);
  });
}
if ($hitlDenyBtn) {
  $hitlDenyBtn.addEventListener("click", () => {
    if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, false);
  });
}

async function executeInActiveTab(func, ...args) {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || tab.url.startsWith("chrome://")) {
    debugLog("RPC", "no active tab or chrome:// page", "err");
    throw new Error("Cannot inject script into this page.");
  }
  debugLog("RPC", "inject tabId=" + tab.id + " url=" + (tab.url || "").slice(0, 50), "rpc");
  const results = await chrome.scripting.executeScript({
    target: { tabId: tab.id },
    func: func,
    args: args
  });
  if (results && results[0]) {
    return results[0].result;
  }
  return null;
}

const CAPTURE_MAX_SLICES = 50;

async function captureFullPage() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab || tab.id == null || tab.url.startsWith("chrome://")) {
    throw new Error("无法截取：当前无有效标签页或为 chrome:// 页面。");
  }
  const getScrollInfo = function () {
    return {
      scrollHeight: Math.max(document.body.scrollHeight || 0, document.documentElement.scrollHeight || 0),
      viewportHeight: document.documentElement.clientHeight || window.innerHeight || 800
    };
  };
  const info = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: getScrollInfo });
  const scrollHeight = info?.[0]?.result?.scrollHeight ?? 0;
  const viewportHeight = info?.[0]?.result?.viewportHeight ?? 800;
  if (scrollHeight <= 0) {
    return { viewportHeight, images: [] };
  }
  const scrollTo = function (y) {
    window.scrollTo(0, y);
  };
  const images = [];
  let y = 0;
  let slices = 0;
  while (y < scrollHeight && slices < CAPTURE_MAX_SLICES) {
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: scrollTo, args: [y] });
    await new Promise((r) => setTimeout(r, 150));
    try {
      const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, { format: "png" });
      if (dataUrl && dataUrl.startsWith("data:image")) {
        const base64 = dataUrl.replace(/^data:image\/\w+;base64,/, "");
        images.push(base64);
      }
    } catch (e) {
      debugLog("RPC", "captureVisibleTab error at y=" + y + " " + (e && e.message), "err");
    }
    y += viewportHeight;
    slices++;
  }
  return { viewportHeight, images };
}

// MCP 工具 run_page_script：预定义脚本注册表，仅执行白名单内 scriptId
const PAGE_SCRIPTS = {
  scroll_to_top: function (params) {
    window.scrollTo(0, 0);
    return "成功：已滚动到页面顶部。";
  },
  scroll_to_bottom: function (params) {
    window.scrollTo(0, document.body.scrollHeight || document.documentElement.scrollHeight);
    return "成功：已滚动到页面底部。";
  },
  get_visible_text: function (params) {
    var text = document.body ? document.body.innerText : "";
    var max = typeof params && params.maxLength > 0 ? params.maxLength : 8000;
    if (text.length > max) text = text.slice(0, max) + "\n...(已截断)";
    return text || "(无文本)";
  },
  get_page_title: function (params) {
    return document.title || "(无标题)";
  }
};

// These functions will run IN THE WEBPAGE CONTEXT
function highlightTextInPage(searchText, color) {
  if (!searchText) return "No text provided";
  
  // A simple highlighting logic. For production, consider using a library like mark.js
  const escapeRegExp = (string) => string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const regex = new RegExp(`(${escapeRegExp(searchText)})`, 'gi');
  let count = 0;
  
  // To avoid infinite loops or breaking scripts, only process body text
  const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
  const nodes = [];
  let node;
  while ((node = walk.nextNode())) {
    if (node.parentNode.nodeName !== 'SCRIPT' && 
        node.parentNode.nodeName !== 'STYLE' && 
        node.parentNode.nodeName !== 'NOSCRIPT' &&
        node.parentNode.nodeName !== 'MARK') {
      nodes.push(node);
    }
  }

  nodes.forEach(n => {
    const text = n.nodeValue;
    const re = new RegExp(`(${escapeRegExp(searchText)})`, 'gi');
    if (re.test(text)) {
      const span = document.createElement('span');
      span.innerHTML = text.replace(new RegExp(`(${escapeRegExp(searchText)})`, 'gi'), `<mark style="background-color: ${color || 'yellow'}; color: #000;">$1</mark>`);
      n.parentNode.replaceChild(span, n);
      count++;
    }
  });

  // 返回明确的中文成功信息，便于 AI 识别并回复用户“已成功”
  return count > 0
    ? "成功：已在当前页面高亮「" + searchText + "」共 " + count + " 处。"
    : "成功：未在页面中找到「" + searchText + "」。";
}

// 整段函数会被注入到页面执行，必须自包含。锚定时插入文档流以随页面滚动；用 try-catch 避免未捕获错误导致超时。
function addFloatingNoteInPage(message, title, anchorText) {
  try {
    if (typeof message !== 'string') message = '';
    var anchorEl = null;
    if (typeof anchorText === 'string' && anchorText.trim()) {
      var walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
      var node;
      while ((node = walk.nextNode())) {
        if (node.nodeValue && node.nodeValue.indexOf(anchorText.trim()) !== -1) {
          var p = node.parentNode;
          if (p && p.nodeName !== 'SCRIPT' && p.nodeName !== 'STYLE') {
            anchorEl = p;
            break;
          }
        }
      }
    }

    var id = 'copilot-note-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
    var note = document.createElement('div');
    note.id = id;
    note.style.maxWidth = '320px';
    note.style.padding = '15px';
    note.style.backgroundColor = '#1a1a1a';
    note.style.color = '#fff';
    note.style.border = '2px solid #6c8cff';
    note.style.borderRadius = '8px';
    note.style.boxShadow = '0 10px 25px rgba(0,0,0,0.5)';
    note.style.zIndex = '999999';
    note.style.fontFamily = 'sans-serif';
    note.style.fontSize = '14px';
    note.style.lineHeight = '1.5';

    if (anchorEl && anchorEl.parentNode) {
      note.style.position = 'absolute';
      note.style.left = '0';
      note.style.bottom = '100%';
      note.style.marginBottom = '8px';
      var wrapper = document.createElement('div');
      wrapper.setAttribute('data-copilot-note-wrapper', id);
      wrapper.style.cssText = 'position:relative;height:0;overflow:visible;pointer-events:none;';
      wrapper.appendChild(note);
      wrapper.style.pointerEvents = '';
      note.style.pointerEvents = 'auto';
      anchorEl.parentNode.insertBefore(wrapper, anchorEl);
      var container = wrapper;
      var closeBtn = document.createElement('button');
      closeBtn.textContent = '×';
      closeBtn.style.background = 'none';
      closeBtn.style.border = 'none';
      closeBtn.style.color = '#999';
      closeBtn.style.cursor = 'pointer';
      closeBtn.style.fontSize = '16px';
      closeBtn.onclick = function() { if (container.parentNode) container.parentNode.removeChild(container); };
      var headerTitle = (typeof title === 'string' && title.trim()) ? title.trim() : '⚡ Office Copilot';
      var header = document.createElement('div');
      header.style.display = 'flex';
      header.style.justifyContent = 'space-between';
      header.style.marginBottom = '8px';
      header.style.fontWeight = 'bold';
      header.style.color = '#6c8cff';
      header.textContent = headerTitle;
      header.appendChild(closeBtn);
      note.appendChild(header);
      var content = document.createElement('div');
      content.textContent = message;
      note.appendChild(content);
      setTimeout(function() {
        var w = document.querySelector('[data-copilot-note-wrapper="' + id + '"]');
        if (w && w.parentNode) w.parentNode.removeChild(w);
      }, 15000);
      return "成功：悬浮提示框已添加（随页面滚动）。";
    }

    note.style.position = 'fixed';
    note.style.top = '20px';
    note.style.right = '20px';
    var headerTitle = (typeof title === 'string' && title.trim()) ? title.trim() : '⚡ Office Copilot';
    var header = document.createElement('div');
    header.style.display = 'flex';
    header.style.justifyContent = 'space-between';
    header.style.marginBottom = '8px';
    header.style.fontWeight = 'bold';
    header.style.color = '#6c8cff';
    header.textContent = headerTitle;
    var closeBtn = document.createElement('button');
    closeBtn.textContent = '×';
    closeBtn.style.background = 'none';
    closeBtn.style.border = 'none';
    closeBtn.style.color = '#999';
    closeBtn.style.cursor = 'pointer';
    closeBtn.style.fontSize = '16px';
    closeBtn.onclick = function() { note.remove(); };
    header.appendChild(closeBtn);
    note.appendChild(header);
    var content = document.createElement('div');
    content.textContent = message;
    note.appendChild(content);
    document.body.appendChild(note);
    setTimeout(function() {
      var el = document.getElementById(id);
      if (el) el.remove();
    }, 15000);
    return "成功：悬浮提示框已添加。";
  } catch (e) {
    return "失败：" + (e && e.message ? e.message : String(e));
  }
}

// ───── Canvas Rendering ─────
async function extractAndRenderCanvas(rawText) {
  const msgs = document.querySelectorAll('.msg--bot');
  if (msgs.length === 0) return;
  const lastMsg = msgs[msgs.length - 1];
  const text = rawText || lastMsg.textContent;
  
  const startIdx = text.indexOf('<html_canvas>');
  const endIdx = text.indexOf('</html_canvas>');
  
  let htmlCode = null;
  if (startIdx !== -1 && endIdx !== -1 && endIdx > startIdx) {
    htmlCode = text.substring(startIdx + 13, endIdx).trim();
    // 隐藏原始代码文本
    const displayHtml = lastMsg.innerHTML.replace(/&lt;html_canvas&gt;[\s\S]*?&lt;\/html_canvas&gt;|<html_canvas>[\s\S]*?<\/html_canvas>/g, '<i>[交互式图表已生成在展示板]</i>');
    lastMsg.innerHTML = displayHtml;
  }
  
  if (currentMode === 'workspace') {
    // Send to workspace tab
    const hasMermaid = text.includes('```mermaid');
    if (htmlCode || hasMermaid || text.length > 500) {
      await sendToWorkspace(htmlCode, text);
    }
  } else {
    // Render in sidepanel
    if (htmlCode) {
      renderCanvas(htmlCode);
    }
  }
}

async function sendToWorkspace(htmlCode, markdown) {
  const url = chrome.runtime.getURL('workspace.html');
  
  const tabs = await chrome.tabs.query({});
  let wsTab = tabs.find(t => t.url === url);
  
  if (!wsTab) {
    wsTab = await chrome.tabs.create({ url: url, active: true });
    // Wait for it to load
    await new Promise(resolve => {
      const listener = (request) => {
        if (request.type === 'WORKSPACE_READY') {
          chrome.runtime.onMessage.removeListener(listener);
          resolve();
        }
      };
      chrome.runtime.onMessage.addListener(listener);
      // Fallback timeout
      setTimeout(() => {
        chrome.runtime.onMessage.removeListener(listener);
        resolve();
      }, 1500);
    });
  }
  
  chrome.tabs.sendMessage(wsTab.id, {
    type: 'RENDER_WORKSPACE',
    htmlCode: htmlCode,
    markdown: htmlCode ? null : markdown
  });
}

function renderCanvas(htmlCode) {
  let container = document.getElementById('canvas-container');
  if (!container) {
    container = document.createElement('div');
    container.id = 'canvas-container';
    container.className = 'canvas-container';
    document.getElementById('messages').appendChild(container);
  } else {
    // 移到最新消息下面
    document.getElementById('messages').appendChild(container);
  }
  
  container.innerHTML = `
    <div class="canvas-header">
      <span>📊 数据展示板</span>
      <button onclick="document.getElementById('canvas-container').style.display='none'">关闭</button>
    </div>
    <iframe sandbox="allow-scripts" class="canvas-frame"></iframe>
  `;
  container.style.display = 'flex';
  
  const iframe = container.querySelector('iframe');
  iframe.srcdoc = htmlCode;
  $messages.scrollTop = $messages.scrollHeight;
}

// ───── UI helpers ─────

function addUserMessage(text) {
  appendMsg("msg--user", text);
}

function addBotMessage(text, isError = false) {
  const div = appendMsg("msg--bot" + (isError ? " msg--error" : ""), "");
  const displayText = isError ? (text ? `⚠️ ${text}` : "⚠️ 请求失败") : text;
  if (typeof marked !== 'undefined') {
    div.innerHTML = marked.parse(displayText);
    if (typeof mermaid !== 'undefined') {
      const mermaidBlocks = div.querySelectorAll('.language-mermaid');
      mermaidBlocks.forEach((block, index) => {
        const id = `mermaid-${Date.now()}-${index}`;
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
  } else {
    div.textContent = displayText;
  }
}

function addSystemMessage(text) {
  appendMsg("msg--system", text);
}

function appendMsg(cls, text) {
  const welcome = $messages.querySelector(".welcome");
  if (welcome) welcome.remove();

  const div = document.createElement("div");
  div.className = `msg ${cls}`;
  div.textContent = text;
  $messages.appendChild(div);
  $messages.scrollTop = $messages.scrollHeight;
  return div;
}

function setStatus(connected) {
  $status.className = connected ? "status status--connected" : "status status--disconnected";
  const $text = $status.querySelector(".status-text");
  if ($text) $text.textContent = connected ? "已连接" : "未连接";
}

function setInputEnabled(enabled) {
  $input.disabled = !enabled;
  $sendBtn.disabled = !enabled;
  if ($stopBtn) $stopBtn.style.display = enabled ? "none" : "flex";
  if ($sendBtn) $sendBtn.style.display = enabled ? "flex" : "none";
  if (enabled) $input.focus();
}

// ───── Input handling ─────

function buildAttachmentsPayload() {
  return attachments.map(a => ({ mimeType: a.mimeType, data: a.data }));
}

async function handleSend() {
  const text = $input.value.trim();
  const hasAttachments = attachments.length > 0;
  if (!text && !hasAttachments) return;
  if (streamingBubble) return;

  const attachmentsPayload = buildAttachmentsPayload();
  addUserMessage(text || (hasAttachments ? "（附图片）" : ""));

  if (currentMode === 'assistant') {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (tab && !tab.url.startsWith('chrome://')) {
        const results = await chrome.scripting.executeScript({
          target: { tabId: tab.id },
          func: () => {
            return {
              title: document.title,
              url: window.location.href,
              content: document.body.innerText.substring(0, 5000)
            };
          }
        });
        if (results && results[0] && results[0].result) {
          sendWithContext(text, results[0].result, attachmentsPayload);
        } else {
          send(text, attachmentsPayload);
        }
      } else {
        send(text, attachmentsPayload);
      }
    } catch (err) {
      console.error("Failed to get page context:", err);
      send(text, attachmentsPayload);
    }
  } else {
    send(text, attachmentsPayload);
  }

  $input.value = "";
  $input.style.height = "auto";
  attachments.length = 0;
  renderAttachmentsPreview();
  $input.focus();
}

function sendWithContext(text, context, attachmentsPayload = null) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  const payload = JSON.stringify({
    type: "text_with_context",
    content: text || "",
    context: context,
    ...(attachmentsPayload && attachmentsPayload.length > 0 ? { attachments: attachmentsPayload } : {})
  });
  ws.send(payload);
  debugLog("WS Send", "type=text_with_context len=" + (text || "").length + " contextTitle=" + (context?.title || "").slice(0, 30) + " attachments=" + (attachmentsPayload?.length || 0), "send");
}

// ───── Attachments (images) ─────

function renderAttachmentsPreview() {
  if (!$attachmentsPreview) return;
  $attachmentsPreview.innerHTML = "";
  if (attachments.length === 0) {
    $attachmentsPreview.style.display = "none";
    return;
  }
  $attachmentsPreview.style.display = "flex";
  attachments.forEach((att, index) => {
    const wrap = document.createElement("div");
    wrap.className = "attachment-thumb-wrap";
    const img = document.createElement("img");
    img.className = "attachment-thumb";
    img.src = "data:" + (att.mimeType || "image/png") + ";base64," + att.data;
    img.alt = "附件";
    const removeBtn = document.createElement("button");
    removeBtn.type = "button";
    removeBtn.className = "attachment-remove";
    removeBtn.title = "移除";
    removeBtn.textContent = "×";
    removeBtn.addEventListener("click", () => {
      attachments.splice(index, 1);
      renderAttachmentsPreview();
    });
    wrap.appendChild(img);
    wrap.appendChild(removeBtn);
    $attachmentsPreview.appendChild(wrap);
  });
}

function addAttachment(mimeType, base64Data) {
  attachments.push({ id: Date.now() + "-" + Math.random(), mimeType: mimeType || "image/png", data: base64Data });
  renderAttachmentsPreview();
}

function addFilesAsAttachments(files) {
  const imageFiles = Array.from(files).filter(f => f.type.startsWith("image/"));
  if (imageFiles.length === 0) return;
  imageFiles.forEach(file => {
    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = reader.result;
      const match = dataUrl.match(/^data:([^;]+);base64,(.+)$/);
      const mime = match ? match[1] : "image/png";
      const data = match ? match[2] : dataUrl.split(",")[1] || "";
      if (data) addAttachment(mime, data);
    };
    reader.readAsDataURL(file);
  });
}

if ($attachBtn && $fileInput) {
  $attachBtn.addEventListener("click", () => $fileInput.click());
  $fileInput.addEventListener("change", (e) => {
    if (e.target.files && e.target.files.length) {
      addFilesAsAttachments(e.target.files);
      e.target.value = "";
    }
  });
}

$input.addEventListener("paste", (e) => {
  const items = e.clipboardData?.items;
  if (!items) return;
  const files = [];
  for (let i = 0; i < items.length; i++) {
    if (items[i].type.startsWith("image/")) files.push(items[i].getAsFile());
  }
  if (files.length) {
    e.preventDefault();
    addFilesAsAttachments(files);
  }
});

const $inputArea = document.querySelector(".input-area");
if ($inputArea) {
  $inputArea.addEventListener("dragover", (e) => {
    e.preventDefault();
    e.stopPropagation();
    $inputArea.classList.add("input-area--drag");
  });
  $inputArea.addEventListener("dragleave", (e) => {
    e.preventDefault();
    e.stopPropagation();
    $inputArea.classList.remove("input-area--drag");
  });
  $inputArea.addEventListener("drop", (e) => {
    e.preventDefault();
    e.stopPropagation();
    $inputArea.classList.remove("input-area--drag");
    if (e.dataTransfer?.files?.length) addFilesAsAttachments(e.dataTransfer.files);
  });
}

$sendBtn.addEventListener("click", handleSend);

if ($stopBtn) {
  $stopBtn.addEventListener("click", () => {
    if (!streamingBubble) return;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: "stop" }));
      debugLog("WS Send", "type=stop", "send");
    }
    finalizeStream();
  });
}

$input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    handleSend();
  }
});

$input.addEventListener("input", () => {
  $input.style.height = "auto";
  $input.style.height = Math.min($input.scrollHeight, 120) + "px";
});

// ───── Boot ─────

connect();
