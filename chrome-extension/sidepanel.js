const WS_URL = "ws://localhost:8765/ws";
const API_BASE = "http://localhost:8765";
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
const $voiceBtn = document.getElementById("voice-btn");
const $status = document.getElementById("status");
const $settingsBtn = document.getElementById("settings-btn");
const $currentPlanLabel = document.getElementById("current-plan-label");
const $currentPageLabel = document.getElementById("current-page-label");
const $planChecklistWrap = document.getElementById("plan-checklist-wrap");
const $planChecklistList = document.getElementById("plan-checklist-list");
const $planChecklistSummary = document.getElementById("plan-checklist-summary");

const $newChatBtn = document.getElementById("new-chat-btn");
const $cancelPlanBtn = document.getElementById("cancel-plan-btn");
const $planStepIndicator = document.getElementById("plan-step-indicator");
const $meetingBtn = document.getElementById("meeting-btn");
const $meetingPanel = document.getElementById("meeting-panel");
const $meetingTimer = document.getElementById("meeting-timer");
const $meetingStopBtn = document.getElementById("meeting-stop-btn");
const $meetingPreview = document.getElementById("meeting-preview");

const STORAGE_PLAN_ID = "copilot_plan_id";
const STORAGE_PLAN_TITLE = "copilot_plan_title";
const STORAGE_PLAN_STEP_INDEX = "copilot_plan_step_index";

if ($settingsBtn) {
  $settingsBtn.addEventListener("click", () => {
    chrome.runtime.openOptionsPage();
  });
}

// ───── New conversation ─────
if ($newChatBtn) {
  $newChatBtn.addEventListener("click", () => {
    sessionStorage.removeItem("copilot_session_id");
    setCurrentPlan(null, null);
    $messages.innerHTML = '<div class="welcome"><p class="welcome-title">你好，我是 Office Copilot 👋</p><p class="welcome-sub">你的本地智能办公助手。输入任何内容开始对话。</p></div>';
    attachments.length = 0;
    if ($attachmentsPreview) { $attachmentsPreview.innerHTML = ""; $attachmentsPreview.style.display = "none"; }
    if (ws) { ws.close(); ws = null; }
    connect();
  });
}

async function getActiveTabTitle() {
  if (typeof chrome === "undefined" || !chrome.tabs) return "(无)";
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab || tab.url?.startsWith("chrome://")) return "(无)";
    return (tab.title && tab.title.trim()) ? tab.title.trim() : "(无标题)";
  } catch {
    return "(无)";
  }
}

function updateCurrentPageLabel(title, sessionId) {
  if (!$currentPageLabel) return;
  const label = title != null && title !== "" ? title : "(无)";
  if (sessionId != null && sessionId !== "") {
    $currentPageLabel.textContent = label + " · " + sessionId;
  } else {
    $currentPageLabel.textContent = label;
  }
}

function sendSetContext(pageTitle) {
  if (!ws || ws.readyState !== WebSocket.OPEN || !pageTitle || pageTitle === "(无)") return;
  const t = pageTitle.length > 200 ? pageTitle.slice(0, 200) : pageTitle;
  ws.send(JSON.stringify({ type: "set_context", pageTitle: t }));
}

function getCurrentPlanId() {
  return sessionStorage.getItem(STORAGE_PLAN_ID) || "";
}

function setCurrentPlan(planId, title) {
  planChecklistSteps = [];
  planChecklistStatus = {};
  if (planId) {
    sessionStorage.setItem(STORAGE_PLAN_ID, planId);
    sessionStorage.setItem(STORAGE_PLAN_TITLE, title || planId);
    sessionStorage.setItem(STORAGE_PLAN_STEP_INDEX, "1");
    if (typeof chrome !== "undefined" && chrome.storage?.local)
      chrome.storage.local.set({ copilot_plan_id: planId, copilot_plan_title: title || planId });
  } else {
    sessionStorage.removeItem(STORAGE_PLAN_ID);
    sessionStorage.removeItem(STORAGE_PLAN_TITLE);
    sessionStorage.removeItem(STORAGE_PLAN_STEP_INDEX);
    if (typeof chrome !== "undefined" && chrome.storage?.local)
      chrome.storage.local.set({ copilot_plan_id: "", copilot_plan_title: "" });
    if ($planChecklistWrap) $planChecklistWrap.style.display = "none";
  }
  if ($currentPlanLabel) $currentPlanLabel.textContent = title || planId || "无";
  if ($currentPlanLabel) $currentPlanLabel.title = planId ? `点击查看计划: ${title || planId}` : "当前计划";
  if ($cancelPlanBtn) $cancelPlanBtn.style.display = planId ? "inline" : "none";
  updatePlanStepIndicator();
}

function updatePlanStepIndicator() {
  if (!$planStepIndicator) return;
  const planId = getCurrentPlanId();
  if (!planId || planChecklistSteps.length === 0) {
    $planStepIndicator.style.display = "none";
    return;
  }
  const step = getPlanCurrentStepIndex();
  const total = planChecklistSteps.length;
  $planStepIndicator.textContent = `(${step}/${total})`;
  $planStepIndicator.style.display = "inline";
}

if ($currentPlanLabel) {
  $currentPlanLabel.addEventListener("click", () => {
    const planId = getCurrentPlanId();
    if (!planId) return;
    if (typeof chrome !== "undefined" && chrome.tabs) {
      chrome.tabs.create({ url: `plans.html?id=${planId}` });
    }
  });
}

if ($cancelPlanBtn) {
  $cancelPlanBtn.addEventListener("click", () => {
    setCurrentPlan(null, null);
    addSystemMessage("已取消当前计划绑定");
  });
}

function getPlanCurrentStepIndex() {
  const v = sessionStorage.getItem(STORAGE_PLAN_STEP_INDEX);
  const n = parseInt(v, 10);
  return (n >= 1) ? n : 1;
}

function setPlanCurrentStepIndex(stepIndex) {
  if (stepIndex >= 1) sessionStorage.setItem(STORAGE_PLAN_STEP_INDEX, String(stepIndex));
}

// ───── Plan checklist (执行进度) ─────
let planChecklistSteps = [];
let planChecklistStatus = {};

function parsePlanStepsFromContent(content) {
  if (!content || typeof content !== "string") return [];
  const steps = [];
  const re = /^#{1,6}\s*步骤\s*(\d+)\s*$/gm;
  let m;
  const indices = [];
  while ((m = re.exec(content)) !== null) indices.push({ num: parseInt(m[1], 10), pos: m.index });
  for (let i = 0; i < indices.length; i++) {
    const start = indices[i].pos;
    const end = i + 1 < indices.length ? indices[i + 1].pos : content.length;
    const block = content.slice(start, end).trim();
    const firstLine = block.split(/\r?\n/)[0] || "";
    const title = firstLine.replace(/^#{1,6}\s*步骤\s*\d+\s*/, "").trim() || "步骤 " + indices[i].num;
    steps.push({ index: indices[i].num, title: title.slice(0, 60) });
  }
  return steps;
}

async function ensurePlanChecklistLoaded(planId) {
  if (planChecklistSteps.length > 0) return;
  try {
    const res = await fetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
    if (!res.ok) return;
    const data = await res.json();
    const content = data.content || "";
    planChecklistSteps = parsePlanStepsFromContent(content);
    planChecklistStatus = {};
    planChecklistSteps.forEach(s => { planChecklistStatus[s.index] = "pending"; });
    renderPlanChecklist();
    updatePlanStepIndicator();
  } catch (e) {
    console.warn("Failed to load plan for checklist", e);
  }
}

function renderPlanChecklist() {
  if (!$planChecklistWrap || !$planChecklistList) return;
  if (planChecklistSteps.length === 0) {
    $planChecklistWrap.style.display = "none";
    return;
  }
  $planChecklistWrap.style.display = "block";
  $planChecklistList.innerHTML = "";
  const total = planChecklistSteps.length;
  const done = Object.values(planChecklistStatus).filter(s => s === "done").length;
  if ($planChecklistSummary) $planChecklistSummary.textContent = `执行进度 (${done}/${total})`;
  planChecklistSteps.forEach(({ index, title }) => {
    const status = planChecklistStatus[index] || "pending";
    const li = document.createElement("li");
    li.className = "plan-step plan-step--" + status;
    li.dataset.stepIndex = String(index);
    const icon = status === "done" ? "✓" : status === "in_progress" ? "◐" : "○";
    li.innerHTML = `<span class="plan-step-icon">${icon}</span> <span class="plan-step-title">步骤 ${index}: ${escapeHtml(title)}</span>`;
    $planChecklistList.appendChild(li);
  });
}

function updateChecklistStep(stepIndex, status) {
  if (!stepIndex || stepIndex < 1) return;
  planChecklistStatus[stepIndex] = status;
  const li = $planChecklistList?.querySelector(`[data-step-index="${stepIndex}"]`);
  if (li) {
    li.className = "plan-step plan-step--" + status;
    const icon = status === "done" ? "✓" : status === "in_progress" ? "◐" : "○";
    const titleEl = li.querySelector(".plan-step-title");
    const title = titleEl ? titleEl.textContent.replace(/^步骤 \d+:\s*/, "") : "";
    li.querySelector(".plan-step-icon").textContent = icon;
  }
  const total = planChecklistSteps.length;
  const done = Object.values(planChecklistStatus).filter(s => s === "done").length;
  if ($planChecklistSummary) $planChecklistSummary.textContent = `执行进度 (${done}/${total})`;
}

function appendPlanCreatedMessage(planId, title, isUpdated) {
  const div = document.createElement("div");
  div.className = "msg msg--system";
  div.textContent = isUpdated
    ? "计划已更新，已在新标签页打开。若需再改请在此继续说明。"
    : "计划已生成，已在新标签页打开。若需修改请在此继续说明。";
  $messages.appendChild(div);
}

function showPlanConfirmDialog(planId, title, onConfirm) {
  const overlay = document.createElement("div");
  overlay.className = "plan-confirm-overlay";
  overlay.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.5);display:flex;align-items:center;justify-content:center;z-index:9999;";
  const box = document.createElement("div");
  box.className = "plan-confirm-box";
  box.style.cssText = "background:var(--bg-primary,#0f172a);border:1px solid var(--border,#334155);border-radius:12px;padding:20px;max-width:360px;box-shadow:0 10px 40px rgba(0,0,0,0.3);";
  box.innerHTML = "<p style='margin:0 0 12px;font-weight:500;'>该计划需您确认后再执行</p><p style='margin:0 0 16px;font-size:13px;color:var(--text-secondary,#94a3b8);'>" + escapeHtml(title || planId || "") + "</p><div style='display:flex;gap:10px;justify-content:flex-end;'>" +
    "<button type='button' class='plan-confirm-cancel' style='padding:8px 16px;border-radius:8px;border:1px solid var(--border);background:transparent;color:var(--text-secondary);cursor:pointer;'>取消</button>" +
    "<button type='button' class='plan-confirm-ok' style='padding:8px 16px;border-radius:8px;border:none;background:var(--accent,#3b82f6);color:#fff;cursor:pointer;'>确认执行</button></div>";
  overlay.appendChild(box);
  function close() {
    overlay.remove();
  }
  box.querySelector(".plan-confirm-cancel").addEventListener("click", () => { close(); });
  box.querySelector(".plan-confirm-ok").addEventListener("click", () => { close(); onConfirm(); });
  overlay.addEventListener("click", (e) => { if (e.target === overlay) close(); });
  document.body.appendChild(overlay);
}

document.addEventListener("DOMContentLoaded", async () => {
  const pageTitle = await getActiveTabTitle();
  updateCurrentPageLabel(pageTitle, getSessionId());
  if (typeof chrome !== "undefined" && chrome.storage?.local) {
    chrome.storage.local.get(["copilot_plan_id", "copilot_plan_title", "copilot_execute_plan_id", "copilot_execute_plan_title"], (r) => {
      if (r.copilot_plan_id) setCurrentPlan(r.copilot_plan_id, r.copilot_plan_title || "");
      else if ($currentPlanLabel) $currentPlanLabel.textContent = "无";
      const execPlanId = r.copilot_execute_plan_id;
      if (execPlanId) {
        const title = r.copilot_execute_plan_title || execPlanId;
        setCurrentPlan(execPlanId, title);
        pendingExecutePlan = true;
        chrome.storage.local.remove(["copilot_execute_plan_id", "copilot_execute_plan_title"]);
      }
    });
    // 计划页点击「执行计划」时写入 copilot_execute_plan_id，侧边栏据此绑定计划并发送执行请求
    chrome.storage.onChanged.addListener((changes, areaName) => {
      if (areaName !== "local" || !changes.copilot_execute_plan_id) return;
      const planId = changes.copilot_execute_plan_id.newValue;
      if (!planId) return;
      const title = (changes.copilot_execute_plan_title?.newValue) || planId;
      setCurrentPlan(planId, title);
      chrome.storage.local.remove(["copilot_execute_plan_id", "copilot_execute_plan_title"], () => {
        if (typeof send === "function") send("请按当前绑定的计划执行");
      });
    });
  } else {
    const planId = getCurrentPlanId();
    const title = sessionStorage.getItem(STORAGE_PLAN_TITLE);
    if ($currentPlanLabel) $currentPlanLabel.textContent = title || planId || "无";
  }
});

let ws = null;
let sessionId = null;
let reconnectDelay = RECONNECT_BASE_MS;
let reconnectTimer = null;
let reconnectAttempts = 0;
/** 断线时未发出去的消息，重连后按顺序自动重发 */
let pendingMessages = [];
/** 计划页触发执行、侧边栏加载时尚未连接，连接后自动发执行请求 */
let pendingExecutePlan = false;
let streamingBubble = null;
const attachments = []; // { mimeType, data (base64), id } for preview

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
  const url = `${WS_URL}?sessionId=${sessionId}&token=${AUTH_TOKEN}&clientType=chrome`;
  ws = new WebSocket(url);

  ws.addEventListener("open", async () => {
    reconnectDelay = RECONNECT_BASE_MS;
    reconnectAttempts = 0;
    setStatus("connected");
    addSystemMessage("已连接到本地服务");
    debugLog("WS", "connected sessionId=" + sessionId, "recv");
    const pageTitle = await getActiveTabTitle();
    updateCurrentPageLabel(pageTitle, sessionId);
    sendSetContext(pageTitle);
    const toFlush = pendingMessages.length;
    while (pendingMessages.length > 0) {
      const m = pendingMessages.shift();
      send(m.text, m.attachmentsPayload);
    }
    if (toFlush > 0) addSystemMessage("已重连，待发消息已自动发出");
    if (pendingExecutePlan && getCurrentPlanId()) {
      pendingExecutePlan = false;
      send("请按当前绑定的计划执行");
    }
  });

  ws.addEventListener("message", (e) => {
    handleMessage(e.data);
  });

  ws.addEventListener("close", () => {
    const wasStreaming = !!streamingBubble;
    ws = null;
    setStatus("disconnected");
    finalizeStream();
    if (wasStreaming) addBotMessage("连接已断开，请检查网络或稍后重试", true);
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
  reconnectAttempts++;
  setStatus("reconnecting");
  if (reconnectAttempts >= 3) {
    setStatus("failed");
  }
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
    connect();
  }, reconnectDelay);
}

function send(text, attachmentsPayload = null) {
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    addBotMessage("连接已断开，消息未发送。请检查网络或刷新侧边栏后重试。", true);
    return;
  }
  const planId = getCurrentPlanId() || undefined;
  const base = { type: "text", content: text || "", mode: "agent", planId };
  if (planId) base.planCurrentStepIndex = getPlanCurrentStepIndex();
  if (attachmentsPayload && attachmentsPayload.length > 0) base.attachments = attachmentsPayload;
  const payload = JSON.stringify(base);
  ws.send(payload);
  debugLog("WS Send", "type=text planId=" + (planId || "-") + " step=" + (planId ? getPlanCurrentStepIndex() : "-") + " len=" + (text || "").length, "send");
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
// 子代理块：默认折叠，内含流式文本 + 子代理内工具调用
let currentSubtaskBlock = null;
let currentSubtaskStreamEl = null;
let currentSubtaskToolsEl = null;
let currentSubtaskToolBlocks = [];
let currentSubtaskToolEndIndex = 0;

function beginStream() {
  removeThinkingIndicator();
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

function appendStreamWarning(text) {
  if (!currentRoundWrapper) beginStream();
  const wrap = currentRoundWrapper;
  if (!wrap) return;
  const notice = document.createElement("div");
  notice.className = "msg msg--stream-warning";
  notice.textContent = (text && String(text).trim()) || "服务端返回了警告";
  wrap.insertBefore(notice, wrap.firstChild);
  if ($messages) $messages.scrollTop = $messages.scrollHeight;
}

let _streamRenderPending = false;
let _streamRenderRafId = null;

function appendStreamChunk(text) {
  if (!streamingBubble) {
    beginStream();
  }
  currentBotMessageRaw += text;

  if (_streamRenderPending) return;
  _streamRenderPending = true;
  _streamRenderRafId = requestAnimationFrame(() => {
    _streamRenderPending = false;
    _streamRenderRafId = null;
    if (!streamingBubble) return;
    if (typeof marked !== 'undefined') {
      streamingBubble.innerHTML = marked.parse(currentBotMessageRaw);
    } else {
      streamingBubble.textContent = currentBotMessageRaw;
    }
    $messages.scrollTop = $messages.scrollHeight;
  });
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
  currentSubtaskBlock = null;
  currentSubtaskStreamEl = null;
  currentSubtaskToolsEl = null;
  currentSubtaskToolBlocks = [];
  currentSubtaskToolEndIndex = 0;
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

    case "stream_warning":
      appendStreamWarning(msg.content);
      break;

    case "stream_end":
      const rawMsg = currentBotMessageRaw;
      finalizeStream();
      extractAndRenderCanvas(rawMsg);
      break;

    case "subtask_start": {
      if (!executionLogBody) beginStream();
      if (!executionLogBody) break;
      const taskDesc = (msg.taskDescription && String(msg.taskDescription).trim()) || "子任务";
      const titleLen = 48;
      const summaryLabel = taskDesc.length <= titleLen ? taskDesc : taskDesc.slice(0, titleLen) + "…";
      const block = document.createElement("details");
      block.className = "subtask-block tool-call-block tool-call--running";
      block.dataset.label = "子代理：" + summaryLabel;
      block.open = false;
      const sum = document.createElement("summary");
      sum.innerHTML = `<span class="tool-status-icon">⏳</span> 子代理：${escapeHtml(summaryLabel)}`;
      block.appendChild(sum);
      const inner = document.createElement("div");
      inner.className = "subtask-inner";
      if (msg.taskDescription) {
        const taskEl = document.createElement("div");
        taskEl.className = "subtask-task";
        taskEl.textContent = "任务：" + (msg.taskDescription || "").trim();
        inner.appendChild(taskEl);
      }
      if (msg.constraints && String(msg.constraints).trim()) {
        const conEl = document.createElement("div");
        conEl.className = "subtask-constraints";
        conEl.textContent = "约束：" + String(msg.constraints).trim();
        inner.appendChild(conEl);
      }
      const streamEl = document.createElement("pre");
      streamEl.className = "subtask-stream";
      inner.appendChild(streamEl);
      const toolsWrap = document.createElement("div");
      toolsWrap.className = "subtask-tools";
      inner.appendChild(toolsWrap);
      block.appendChild(inner);
      executionLogBody.appendChild(block);
      currentSubtaskBlock = block;
      currentSubtaskStreamEl = streamEl;
      currentSubtaskToolsEl = toolsWrap;
      currentSubtaskToolBlocks = [];
      currentSubtaskToolEndIndex = 0;
      updateExecutionLogCount();
      break;
    }

    case "subtask_chunk": {
      if (!currentSubtaskBlock || !currentSubtaskStreamEl) break;
      const t = msg.content != null ? String(msg.content) : "";
      if (t) {
        currentSubtaskStreamEl.textContent += t;
        if (currentSubtaskBlock.open) {
          currentSubtaskStreamEl.scrollTop = currentSubtaskStreamEl.scrollHeight;
        }
      }
      break;
    }

    case "subtask_end": {
      if (currentSubtaskBlock) {
        const sum = currentSubtaskBlock.querySelector("summary");
        if (sum) sum.innerHTML = `<span class="tool-status-icon">✓</span> 子代理（已完成）`;
        currentSubtaskBlock.classList.remove("tool-call--running");
        currentSubtaskBlock.classList.add("tool-call--done");
        if (msg.content && String(msg.content).trim() && currentSubtaskStreamEl) {
          const existing = currentSubtaskStreamEl.textContent.trim();
          if (!existing) currentSubtaskStreamEl.textContent = String(msg.content).trim();
        }
      }
      currentSubtaskBlock = null;
      currentSubtaskStreamEl = null;
      currentSubtaskToolsEl = null;
      currentSubtaskToolBlocks = [];
      currentSubtaskToolEndIndex = 0;
      break;
    }

    case "tool_invocation_start": {
      if (msg.plugin === "Plan" && msg.function === "execute_plan_step" && msg.planStepIndex) {
        const planId = getCurrentPlanId();
        if (planId) {
          ensurePlanChecklistLoaded(planId).then(() => updateChecklistStep(msg.planStepIndex, "in_progress"));
        }
      }
      const label = msg.summary || `正在执行: ${msg.plugin || ""}.${msg.function || ""}`;
      const isSubtask = msg.isSubtask === true;
      const parentBody = isSubtask ? currentSubtaskToolsEl : executionLogBody;
      if (!parentBody) break;
      const block = document.createElement("details");
      block.className = "tool-call-block tool-call--running" + (isSubtask ? " subtask-tool-block" : "");
      block.dataset.label = label;
      const sum = document.createElement("summary");
      sum.innerHTML = `<span class="tool-status-icon">⏳</span> ${escapeHtml(label)}`;
      block.appendChild(sum);
      const out = document.createElement("pre");
      out.className = "tool-call-output";
      block.appendChild(out);
      parentBody.appendChild(block);
      if (isSubtask) {
        currentSubtaskToolBlocks.push(block);
      } else {
        currentRoundToolBlocks.push(block);
        block.open = true;
      }
      updateExecutionLogCount();
      break;
    }

    case "tool_invocation_end": {
      if (msg.plugin === "Plan" && msg.function === "execute_plan_step" && msg.planStepIndex) {
        updateChecklistStep(msg.planStepIndex, msg.success === true ? "done" : "pending");
        if (msg.success === true) {
          setPlanCurrentStepIndex(msg.planStepIndex + 1);
        }
      }
      const isSubtask = msg.isSubtask === true;
      const block = isSubtask ? currentSubtaskToolBlocks[currentSubtaskToolEndIndex] : currentRoundToolBlocks[currentToolEndIndex];
      if (block) {
        const contentRaw = (msg.content && String(msg.content).trim()) || "";
        const looksLikeError = (c) => {
          if (!c) return false;
          return c.startsWith("[错误]") || c.startsWith("[保存失败]") || c.startsWith("[记忆未启用]") || c.startsWith("[无效]") || c.startsWith("[MCP Error]") || c.startsWith("[MCP Client Exception]") || c.startsWith("[系统拦截]") || c.startsWith("[检索失败]") || c.startsWith("[创建失败]") || c.startsWith("[更新失败]") || c.startsWith("[生成计划失败]") || c.startsWith("[执行步骤失败]");
        };
        const ok = msg.success === true && !looksLikeError(contentRaw);
        const name = `${msg.plugin || ""}.${msg.function || ""}`;
        const content = contentRaw;
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
        if (!ok && content && (content.includes("未配置") || content.includes("请在设置") || content.includes("API Key") || content.includes("API_KEY"))) {
          let btn = block.querySelector(".tool-call-goto-settings");
          if (!btn) {
            btn = document.createElement("button");
            btn.className = "tool-call-goto-settings";
            btn.textContent = "前往设置";
            btn.type = "button";
            btn.style.cssText = "margin-top:8px;padding:6px 12px;font-size:12px;cursor:pointer;background:var(--accent, #3b82f6);color:#fff;border:none;border-radius:6px;";
            btn.addEventListener("click", () => { if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.openOptionsPage) chrome.runtime.openOptionsPage(); });
            block.appendChild(btn);
          }
        }
      }
      if (isSubtask) currentSubtaskToolEndIndex++; else currentToolEndIndex++;
      break;
    }

    case "echo":
    case "text":
      addBotMessage(msg.content);
      break;

    case "pong":
      break;

    case "error":
      removeThinkingIndicator();
      finalizeStream();
      addBotMessage((msg.content && String(msg.content).trim()) || "请求失败，请稍后重试", true);
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

    case "plan_created":
    case "plan_updated": {
      const planId = msg.planId || "";
      const title = msg.title || "新计划";
      const requiresConfirm = msg.requiresUserConfirmation === true;
      if (planId) {
        if (msg.type === "plan_created" && requiresConfirm) {
          showPlanConfirmDialog(planId, title, () => {
            setCurrentPlan(planId, title);
            appendPlanCreatedMessage(planId, title, true);
            chrome.tabs.create({ url: chrome.runtime.getURL("plans.html?id=" + encodeURIComponent(planId)) });
          });
        } else {
          setCurrentPlan(planId, title);
          appendPlanCreatedMessage(planId, title, msg.type === "plan_updated");
          chrome.tabs.create({ url: chrome.runtime.getURL("plans.html?id=" + encodeURIComponent(planId)) });
        }
        const welcome = $messages.querySelector(".welcome");
        if (welcome) welcome.remove();
        $messages.scrollTop = $messages.scrollHeight;
      }
      break;
    }

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
const $hitlAddToListBtn = document.getElementById("hitl-add-to-list-btn");
const $hitlDenyBtn = document.getElementById("hitl-deny-btn");

function handleConfirmRequest(msg) {
  const requestId = msg.id || msg.requestId;
  const action = msg.content || msg.action || "未知操作";
  const hitlKind = msg.hitlKind;
  if (!requestId) {
    debugLog("HITL", "confirm_request missing id", "err");
    return;
  }
  pendingConfirmId = requestId;
  if ($hitlAction) $hitlAction.textContent = action;
  const showAddToList = hitlKind === "run_command" || hitlKind === "run_page_script";
  if ($hitlAddToListBtn) $hitlAddToListBtn.style.display = showAddToList ? "" : "none";
  if ($hitlOverlay) {
    $hitlOverlay.style.display = "flex";
    $hitlOverlay.setAttribute("aria-hidden", "false");
  }
  debugLog("HITL", "confirm_request id=" + requestId + " action=" + action.slice(0, 50), "recv");
}

function sendConfirmResponse(id, allowed, addToAllowList) {
  if (!id) return;
  if (ws && ws.readyState === WebSocket.OPEN) {
    const payload = JSON.stringify({ type: "confirm_response", id, allowed, addToAllowList: !!addToAllowList });
    ws.send(payload);
    debugLog("WS Send", "type=confirm_response id=" + id + " allowed=" + allowed + " addToAllowList=" + !!addToAllowList, "send");
  }
  pendingConfirmId = null;
  if ($hitlOverlay) {
    $hitlOverlay.style.display = "none";
    $hitlOverlay.setAttribute("aria-hidden", "true");
  }
}

if ($hitlAllowBtn) {
  $hitlAllowBtn.addEventListener("click", () => {
    if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true, false);
  });
}
if ($hitlAddToListBtn) {
  $hitlAddToListBtn.addEventListener("click", () => {
    if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true, true);
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
  
  const hasMermaid = text.includes('```mermaid');
  const isComplex = htmlCode || hasMermaid || text.length > 500;
  if (isComplex) {
    await sendToWorkspace(htmlCode, text);
  } else if (htmlCode) {
    renderCanvas(htmlCode);
  }
}

async function sendToWorkspace(htmlCode, markdown) {
  const url = chrome.runtime.getURL('workspace.html');

  const tabs = await chrome.tabs.query({});
  let wsTab = tabs.find(t => t.url === url);

  if (!wsTab) {
    wsTab = await chrome.tabs.create({ url: url, active: true });
    await new Promise(resolve => {
      const listener = (request) => {
        if (request.type === 'WORKSPACE_READY') {
          chrome.runtime.onMessage.removeListener(listener);
          resolve();
        }
      };
      chrome.runtime.onMessage.addListener(listener);
      setTimeout(() => {
        chrome.runtime.onMessage.removeListener(listener);
        resolve();
      }, 1500);
    });
  }

  chrome.tabs.sendMessage(wsTab.id, {
    type: 'RENDER_WORKSPACE',
    htmlCode: htmlCode || null,
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

const MAX_VISIBLE_MESSAGES = 100;

function appendMsg(cls, text) {
  const welcome = $messages.querySelector(".welcome");
  if (welcome) welcome.remove();

  const div = document.createElement("div");
  div.className = `msg ${cls}`;
  div.textContent = text;
  $messages.appendChild(div);
  $messages.scrollTop = $messages.scrollHeight;
  trimOldMessages();
  return div;
}

function trimOldMessages() {
  const msgs = $messages.querySelectorAll(".msg");
  if (msgs.length <= MAX_VISIBLE_MESSAGES) return;
  const existing = $messages.querySelector(".msg--collapsed-notice");
  if (existing) existing.remove();
  const toRemove = msgs.length - MAX_VISIBLE_MESSAGES;
  for (let i = 0; i < toRemove; i++) {
    msgs[i].remove();
  }
  const notice = document.createElement("div");
  notice.className = "msg msg--collapsed-notice msg--system";
  notice.textContent = `(${toRemove} 条早期消息已折叠)`;
  $messages.prepend(notice);
}

function setStatus(state) {
  if (state === true) state = "connected";
  if (state === false) state = "disconnected";
  const map = {
    connected: { cls: "status status--connected", text: "已连接" },
    disconnected: { cls: "status status--disconnected", text: "未连接" },
    reconnecting: { cls: "status status--reconnecting", text: "正在重连…" },
    failed: { cls: "status status--disconnected", text: "无法连接" }
  };
  const s = map[state] || map.disconnected;
  $status.className = s.cls;
  const $text = $status.querySelector(".status-text");
  if ($text) $text.textContent = s.text;
  if (state === "failed") {
    $status.title = "无法连接到后台服务，请确认 OfficeCopilot.Server 已启动";
  } else {
    $status.title = "连接状态";
  }
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

  if (!ws || ws.readyState !== WebSocket.OPEN) {
    addUserMessage(text || (hasAttachments ? "（附图片）" : ""));
    pendingMessages.push({ text, attachmentsPayload });
    addBotMessage("连接已断开，正在重连并自动重发…", true);
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    connect();
    $input.value = "";
    $input.style.height = "auto";
    attachments.length = 0;
    renderAttachmentsPreview();
    $input.focus();
    return;
  }

  addUserMessage(text || (hasAttachments ? "（附图片）" : ""));

  send(text, attachmentsPayload);
  showThinkingIndicator();

  $input.value = "";
  $input.style.height = "auto";
  attachments.length = 0;
  renderAttachmentsPreview();
  $input.focus();
}

let thinkingBubble = null;
function showThinkingIndicator() {
  removeThinkingIndicator();
  thinkingBubble = document.createElement("div");
  thinkingBubble.className = "msg msg--bot msg--thinking";
  thinkingBubble.textContent = "正在思考";
  $messages.appendChild(thinkingBubble);
  $messages.scrollTop = $messages.scrollHeight;
}
function removeThinkingIndicator() {
  if (thinkingBubble) { thinkingBubble.remove(); thinkingBubble = null; }
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

// ───── 语音输入（Web Speech API）─────
(function initVoiceInput() {
  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SpeechRecognition || !$voiceBtn || !$input) return;

  let recognition = null;
  let isListening = false;

  function setListening(flag) {
    isListening = flag;
    if ($voiceBtn) $voiceBtn.classList.toggle("recording", flag);
  }

  $voiceBtn.addEventListener("click", () => {
    if (isListening) {
      if (recognition) recognition.stop();
      setListening(false);
      return;
    }

    try {
      recognition = new SpeechRecognition();
      recognition.continuous = true;
      recognition.interimResults = true;
      recognition.lang = "zh-CN";

      recognition.onresult = (e) => {
        for (let i = e.resultIndex; i < e.results.length; i++) {
          if (!e.results[i].isFinal) continue;
          const transcript = e.results[i][0].transcript;
          if (!transcript) continue;
          const cur = $input.value || "";
          $input.value = (cur ? cur + " " : "") + transcript;
        }
      };

      recognition.onerror = (e) => {
        setListening(false);
        const msg = e.error === "not-allowed"
          ? "语音输入被拒绝（请允许麦克风权限）。"
          : e.error === "no-speech"
            ? "未检测到语音，已停止。"
            : e.error === "network"
              ? "网络错误，请检查后重试。"
              : "语音识别错误：" + (e.error || "未知");
        if ($status) $status.textContent = msg;
        else alert(msg);
      };

      recognition.onend = () => setListening(false);

      recognition.start();
      setListening(true);
      if ($status) $status.textContent = "正在听… 再次点击麦克风可停止";
    } catch (err) {
      const msg = "无法启动语音识别：" + (err && err.message ? err.message : String(err));
      if ($status) $status.textContent = msg;
      else alert(msg);
    }
  });
})();

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

// ───── Meeting Listener ─────

(function initMeetingListener() {
  if (!$meetingBtn || !$meetingPanel || !$meetingStopBtn) return;

  let mediaRecorder = null;
  let audioStream = null;
  let meetingTranscript = "";
  let meetingStartTime = null;
  let timerInterval = null;
  let chunkInterval = null;
  let isProcessing = false;

  function formatTime(ms) {
    const s = Math.floor(ms / 1000);
    const mm = String(Math.floor(s / 60)).padStart(2, "0");
    const ss = String(s % 60).padStart(2, "0");
    return mm + ":" + ss;
  }

  function updateTimer() {
    if (!meetingStartTime) return;
    $meetingTimer.textContent = formatTime(Date.now() - meetingStartTime);
  }

  async function transcribeChunk(blob) {
    const formData = new FormData();
    formData.append("file", blob, "chunk.webm");
    try {
      const res = await fetch(API_BASE + "/api/transcribe", { method: "POST", body: formData });
      if (!res.ok) return "";
      const data = await res.json();
      return (data.ok !== false && data.text) ? data.text : "";
    } catch {
      return "";
    }
  }

  async function startMeeting() {
    try {
      audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
      addBotMessage("无法访问麦克风：" + (err.message || "权限被拒绝"), true);
      return;
    }

    meetingTranscript = "";
    meetingStartTime = Date.now();
    $meetingPanel.style.display = "block";
    $meetingPreview.textContent = "";
    $meetingBtn.classList.add("active");
    timerInterval = setInterval(updateTimer, 1000);

    const mimeType = MediaRecorder.isTypeSupported("audio/webm;codecs=opus") ? "audio/webm;codecs=opus" : "audio/webm";
    mediaRecorder = new MediaRecorder(audioStream, { mimeType });

    mediaRecorder.ondataavailable = async (e) => {
      if (e.data.size === 0) return;
      const text = await transcribeChunk(e.data);
      if (text) {
        meetingTranscript += (meetingTranscript ? "\n" : "") + text;
        $meetingPreview.textContent = meetingTranscript.slice(-500);
        $meetingPreview.scrollTop = $meetingPreview.scrollHeight;
      }
    };

    mediaRecorder.start();
    chunkInterval = setInterval(() => {
      if (mediaRecorder && mediaRecorder.state === "recording") {
        mediaRecorder.requestData();
      }
    }, 30000);

    addSystemMessage("会议监听已开始，每 30 秒自动转录一次");
  }

  async function stopMeeting() {
    if (isProcessing) return;
    isProcessing = true;
    $meetingStopBtn.disabled = true;
    $meetingStopBtn.textContent = "处理中…";

    if (chunkInterval) { clearInterval(chunkInterval); chunkInterval = null; }
    if (timerInterval) { clearInterval(timerInterval); timerInterval = null; }

    if (mediaRecorder && mediaRecorder.state === "recording") {
      await new Promise(resolve => {
        mediaRecorder.onstop = resolve;
        mediaRecorder.requestData();
        setTimeout(() => mediaRecorder.stop(), 200);
      });
      await new Promise(r => setTimeout(r, 500));
    }

    if (audioStream) {
      audioStream.getTracks().forEach(t => t.stop());
      audioStream = null;
    }

    $meetingPanel.style.display = "none";
    $meetingBtn.classList.remove("active");
    $meetingStopBtn.disabled = false;
    $meetingStopBtn.textContent = "结束并总结";
    isProcessing = false;

    if (!meetingTranscript.trim()) {
      addSystemMessage("会议监听已结束，未识别到语音内容");
      return;
    }

    const duration = meetingStartTime ? formatTime(Date.now() - meetingStartTime) : "??:??";
    meetingStartTime = null;

    const timestamp = new Date().toISOString().slice(0, 16).replace(/[-:T]/g, "").replace(/(\d{8})(\d{4})/, "$1_$2");
    const dataId = "meeting_" + timestamp;

    addSystemMessage("会议监听已结束（" + duration + "），正在生成会议纪要…");

    if (meetingTranscript.length > 3000) {
      const saveMsg = `请先使用 accurate_data_write 将以下会议录音文本保存（id=${dataId}），然后基于该内容生成会议纪要（包括会议主题、讨论要点、决议和待办事项）。\n\n录音文本：\n${meetingTranscript}`;
      send(saveMsg);
    } else {
      send("请根据以下会议录音内容生成会议纪要，包括会议主题、讨论要点、决议和待办事项：\n\n" + meetingTranscript);
    }
  }

  $meetingBtn.addEventListener("click", () => {
    if (mediaRecorder && mediaRecorder.state === "recording") {
      stopMeeting();
    } else {
      startMeeting();
    }
  });

  $meetingStopBtn.addEventListener("click", () => {
    stopMeeting();
  });
})();

// ───── Boot ─────

function ensureConnectionOnVisible() {
  if (ws && ws.readyState === WebSocket.OPEN) return;
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  connect();
}

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") ensureConnectionOnVisible();
});
window.addEventListener("focus", ensureConnectionOnVisible);

connect();
