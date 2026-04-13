let WS_URL = "ws://127.0.0.1:8765/ws";
let API_BASE = "http://127.0.0.1:8765";
const COPILOT_TOKEN_STORAGE_KEY = "localServiceAuthToken";

var tasklySidepanelApiReady = null;
/** 避免在「后台晚启动」场景下反复弹出同一条气泡提示 */
let tasklyLocalServiceWaitHintShown = false;

function tasklyEnsureApiBase() {
  if (tasklySidepanelApiReady) return tasklySidepanelApiReady;
  tasklySidepanelApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(
    typeof chrome !== "undefined" && chrome.storage && chrome.storage.local ? chrome.storage.local : null
  )
    .then(function (r) {
      var hw = TasklyLocalService.tasklyHttpWsFromBase(r.baseUrl);
      API_BASE = hw.apiBase;
      WS_URL = hw.wsUrl;
    })
    .catch(function (err) {
      // 解析失败时勿永久缓存 rejected，否则后台稍后启动也会一直命中旧失败，无法重新扫端口
      tasklySidepanelApiReady = null;
      throw err;
    });
  return tasklySidepanelApiReady;
}

function formatLocalServiceConnectError(err) {
  if (!err) return "";
  var name = err.name || "";
  var msg = err.message || String(err);
  if (name === "AbortError" || /aborted/i.test(msg)) {
    return "本机服务暂未响应（常见原因：后台尚未启动）。已自动排队重试。";
  }
  return msg;
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

/** 本机 loopback：若 chrome.storage 尚无密钥，从后台引导接口写入（与选项页保存的 user-config / appsettings 一致） */
function ensureLocalServiceTokenFromBootstrap() {
  return tasklyEnsureApiBase().then(function () {
  return fetch(API_BASE + "/api/bootstrap/local-service-auth")
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
const $voiceHint = document.getElementById("voice-hint");
const $status = document.getElementById("status");
const $settingsBtn = document.getElementById("settings-btn");
const $currentPlanLabel = document.getElementById("current-plan-label");
const $currentPageLabel = document.getElementById("current-page-label");
const $planChecklistWrap = document.getElementById("plan-checklist-wrap");
const $planChecklistList = document.getElementById("plan-checklist-list");
const $planChecklistSummary = document.getElementById("plan-checklist-summary");

const $newChatBtn = document.getElementById("new-chat-btn");
const $historyChatBtn = document.getElementById("history-chat-btn");
const $historyOverlay = document.getElementById("history-overlay");
const $historyOverlayBackdrop = document.getElementById("history-overlay-backdrop");
const $historyOverlayClose = document.getElementById("history-overlay-close");
const $historyList = document.getElementById("history-list");
const $historyError = document.getElementById("history-error");
const $historyLoadMore = document.getElementById("history-load-more");
const $agentProfileSelect = document.getElementById("agent-profile-select");
/** 与 WS query、设置页 <code>activeAgentProfileId</code> 对齐 */
const STORAGE_ACTIVE_AGENT_PROFILE_ID = "activeAgentProfileId";
let _suppressAgentProfileSelectChange = false;
const $cancelPlanBtn = document.getElementById("cancel-plan-btn");
const $planStepIndicator = document.getElementById("plan-step-indicator");
const $meetingBtn = document.getElementById("meeting-btn");
const $meetingPanel = document.getElementById("meeting-panel");
const $meetingTimer = document.getElementById("meeting-timer");
const $meetingStopBtn = document.getElementById("meeting-stop-btn");
const $meetingDownloadBtn = document.getElementById("meeting-download-btn");
const $meetingExportHintBtn = document.getElementById("meeting-export-hint-btn");
const $meetingPreview = document.getElementById("meeting-preview");

const STORAGE_PLAN_ID = "copilot_plan_id";
const STORAGE_PLAN_TITLE = "copilot_plan_title";
const STORAGE_PLAN_STEP_INDEX = "copilot_plan_step_index";

/**
 * 在浏览器中打开与本扩展相关的权限/站点设置（麦克风等）。
 * 优先打开 Chrome「网站设置」中本扩展条目；若被策略拦截则打开扩展程序详情页。不再 fallback 到扩展选项页（与麦克风无关，易误导）。
 */
function openTasklyExtensionPermissionSettings() {
  function reportFailure(message) {
    const msg = message || "无法打开权限设置页。";
    try {
      addSystemMessage(msg);
    } catch {
      alert(msg);
    }
  }
  if (typeof chrome === "undefined" || !chrome.runtime || !chrome.runtime.id) {
    reportFailure("当前环境无法打开权限设置。");
    return;
  }
  if (!chrome.tabs || !chrome.tabs.create) {
    reportFailure("无法打开权限页：tabs API 不可用。请手动在 Chrome 中打开「设置 → 隐私与安全 → 网站设置 → 麦克风」，找到本扩展对应站点并允许。");
    return;
  }
  const id = chrome.runtime.id;
  const siteParam = encodeURIComponent(`chrome-extension://${id}/`);
  const origin = `chrome-extension://${id}/`;
  const urls = [
    `chrome://settings/content/siteDetails?site=${siteParam}`,
    `chrome://extensions/?id=${id}`
  ];
  function tryUrl(index) {
    if (index >= urls.length) {
      reportFailure(
        "无法在浏览器中自动打开权限页（可能被策略拦截）。请手动：打开 Chrome「设置 → 隐私与安全 → 网站设置 → 麦克风」，在列表中找到 " +
          origin +
          " 并允许麦克风；或在地址栏访问 chrome://extensions/ 进入本扩展详情检查权限。"
      );
      return;
    }
    chrome.tabs.create({ url: urls[index] }, () => {
      if (chrome.runtime.lastError) {
        tryUrl(index + 1);
      }
    });
  }
  tryUrl(0);
}

/** 打开扩展选项页：优先 openOptionsPage，失败则用新标签打开 options.html，并暴露错误（避免静默失败）。 */
function openTasklyOptionsPage() {
  function reportFailure(message) {
    const msg = message || "无法打开设置页。";
    try {
      addSystemMessage(msg);
    } catch {
      alert(msg);
    }
  }
  if (typeof chrome === "undefined" || !chrome.runtime) {
    reportFailure("当前环境无法打开扩展设置（chrome.runtime 不可用）。");
    return;
  }
  const optionsUrl = chrome.runtime.getURL("options.html");
  function tryTabsCreate() {
    if (!chrome.tabs || !chrome.tabs.create) {
      reportFailure("无法打开设置页：tabs API 不可用。");
      return;
    }
    chrome.tabs.create({ url: optionsUrl }, () => {
      if (chrome.runtime.lastError) {
        reportFailure(chrome.runtime.lastError.message || "新标签页打开 options.html 失败。");
      }
    });
  }
  if (chrome.runtime.openOptionsPage) {
    chrome.runtime.openOptionsPage(() => {
      if (chrome.runtime.lastError) {
        tryTabsCreate();
      }
    });
  } else {
    tryTabsCreate();
  }
}

if ($settingsBtn) {
  $settingsBtn.addEventListener("click", () => {
    openTasklyOptionsPage();
  });
}

/** 与新建对话、删除当前历史会话共用（历史对话块内 WELCOME_INNER_HTML 与此相同文案）。 */
const WELCOME_INNER_HTML_NEW_CHAT =
  '<div class="welcome"><p class="welcome-title">你好，我是 Office Copilot 👋</p><p class="welcome-sub">你的本地智能办公助手；浏览器上下文以<strong>当前窗口中当前活动标签</strong>为准（切换标签后标题会同步），并非只绑定首次打开时那一页。标签页脚本、截图、会议监听等仅在本 Chrome 扩展中可用；开发联调请在侧栏右键「检查」打开 DevTools 查看 Console。WPS/Office 任务窗格连接同一后台。</p></div>';

// ───── New conversation ─────
if ($newChatBtn) {
  $newChatBtn.addEventListener("click", () => {
    sessionStorage.removeItem("copilot_session_id");
    clearConversationTitleStorage();
    setCurrentPlan(null, null);
    $messages.innerHTML = WELCOME_INNER_HTML_NEW_CHAT;
    attachments.length = 0;
    if ($attachmentsPreview) { $attachmentsPreview.innerHTML = ""; $attachmentsPreview.style.display = "none"; }
    if (ws) { ws.close(); ws = null; }
    connect();
  });
}

if ($agentProfileSelect) {
  $agentProfileSelect.addEventListener("change", function () {
    if (_suppressAgentProfileSelectChange) return;
    const v = ($agentProfileSelect.value || "default").trim() || "default";
    const o = {};
    o[STORAGE_ACTIVE_AGENT_PROFILE_ID] = v;
    chrome.storage.local.set(o, function () {
      sessionStorage.removeItem("copilot_session_id");
      clearConversationTitleStorage();
      setCurrentPlan(null, null);
      if ($messages) $messages.innerHTML = WELCOME_INNER_HTML_NEW_CHAT;
      attachments.length = 0;
      if ($attachmentsPreview) {
        $attachmentsPreview.innerHTML = "";
        $attachmentsPreview.style.display = "none";
      }
      if (ws) {
        ws.close();
        ws = null;
      }
      connect();
    });
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

/** 当前窗口活动标签标题与 WS set_context 同步（防抖；监听器仅注册一次） */
const ACTIVE_TAB_CONTEXT_DEBOUNCE_MS = 200;
let activeTabContextDebounceTimer = null;
let activeTabContextListenersRegistered = false;

function scheduleSyncActiveTabContext() {
  if (activeTabContextDebounceTimer != null) clearTimeout(activeTabContextDebounceTimer);
  activeTabContextDebounceTimer = setTimeout(function () {
    activeTabContextDebounceTimer = null;
    void syncActiveTabContextNow();
  }, ACTIVE_TAB_CONTEXT_DEBOUNCE_MS);
}

async function syncActiveTabContextNow() {
  const title = await getActiveTabTitle();
  // 顶栏显示对话摘要，不再用标签页标题，避免与历史会话混淆；仍向后台同步活动标签标题供 Browser 等工具使用。
  sendSetContext(title);
}

function initActiveTabContextSync() {
  if (typeof chrome === "undefined" || !chrome.tabs?.onActivated || !chrome.tabs?.onUpdated) return;
  if (activeTabContextListenersRegistered) return;
  activeTabContextListenersRegistered = true;

  chrome.tabs.onActivated.addListener(function () {
    scheduleSyncActiveTabContext();
  });

  chrome.tabs.onUpdated.addListener(function (tabId, changeInfo) {
    if (changeInfo.title === undefined && changeInfo.status !== "complete") return;
    chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) {
      const cur = tabs && tabs[0];
      if (!cur || cur.id !== tabId) return;
      scheduleSyncActiveTabContext();
    });
  });
}

function truncateTabListUrl(url, maxLen) {
  const s = url != null ? String(url) : "";
  if (s.length <= maxLen) return s;
  return s.slice(0, maxLen) + "…";
}

function getCurrentPlanId() {
  return sessionStorage.getItem(STORAGE_PLAN_ID) || "";
}

function setCurrentPlan(planId, title) {
  planChecklistSteps = [];
  planChecklistStatus = {};
  planChecklistLoadedPlanId = null;
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
      chrome.tabs.create({
        url: chrome.runtime.getURL("plans.html?id=" + encodeURIComponent(planId)),
      });
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
/** 上次拉取 checklist 对应的 planId；切换计划或 plan_updated 时需重拉 */
let planChecklistLoadedPlanId = null;

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
  if (!planId) return;
  if (planChecklistSteps.length > 0 && planChecklistLoadedPlanId === planId) return;
  planChecklistLoadedPlanId = planId;
  try {
    await tasklyEnsureApiBase();
    const res = await tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
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

function appendPlanCreatedMessage(planId, title) {
  const div = document.createElement("div");
  div.className = "msg msg--system";
  div.textContent = "计划已生成，已在新标签页打开。可在计划页审阅编辑；确认后点击「确认并开始执行」以在侧栏开始按步执行。";
  $messages.appendChild(div);
}

function appendPlanUpdatedMessage(planId, title) {
  const div = document.createElement("div");
  div.className = "msg msg--system";
  div.textContent = "计划内容已更新，已刷新已打开的计划页标签（如有）。若侧栏已绑定该计划，执行进度列表已同步。";
  $messages.appendChild(div);
}

function reloadOpenPlanTabsForPlanId(planId) {
  if (typeof chrome === "undefined" || !chrome.tabs || !planId) return;
  const needle = "plans.html?id=" + encodeURIComponent(planId);
  chrome.tabs.query({}, (tabs) => {
    for (const t of tabs || []) {
      const u = t.url || "";
      if (u.includes(needle)) chrome.tabs.reload(t.id);
    }
  });
}

document.addEventListener("DOMContentLoaded", async () => {
  refreshConversationTitleLabel();
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

  initAtModeUI();
  initActiveTabContextSync();
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
/** 收到 cross_agent_task 后自动发一轮用户消息以拉取服务端待办（与 office/wps 侧约定一致） */
let crossAgentAutoRunLock = false;
let crossAgentAutoRunQueued = false;
const CROSS_AGENT_AUTO_TRIGGER_TEXT =
  "请根据系统说明中「来自其他端的待办」逐项执行；每完成一项请调用 complete_cross_agent_task 标记完成。除待办外请勿延伸闲聊。";
const attachments = []; // { mimeType, data (base64), id } for preview

// ───── @ Mode (tool/skill chooser) ─────
let atModeLoading = null;
let atModeLoaded = false;
let atModeCandidates = []; // { group, label, internal }
/** 拉取工具/技能列表失败时的说明（展示在 @ 面板内） */
let atModeLoadError = "";

function sanitizeSkillFunctionName(skillId) {
  // Keep in sync with backend ChatService.SanitizeSkillFunctionName
  if (!skillId) return "Skill";
  let s = String(skillId).trim().replace(/-/g, "_").replace(/\//g, "_").replace(/ /g, "_");
  let out = "";
  let prevUnderscore = false;
  for (const c of s) {
    if ((/[A-Za-z0-9_]/).test(c)) {
      out += c;
      prevUnderscore = false;
    } else if (!prevUnderscore) {
      out += "_";
      prevUnderscore = true;
    }
  }
  out = out.replace(/^_+|_+$/g, "");
  return out ? out : "Skill";
}

async function loadAtModeCandidates() {
  if (atModeLoaded) return;
  if (atModeLoading) return atModeLoading;
  atModeLoading = (async () => {
    atModeLoadError = "";
    try {
      await tasklyEnsureApiBase();
      const [builtinRes, skillsRes] = await Promise.all([
        tasklyFetch(API_BASE + "/api/tools/builtin"),
        tasklyFetch(API_BASE + "/api/skills"),
      ]);
      const loadErrs = [];
      if (!builtinRes.ok) loadErrs.push("内置工具接口 HTTP " + builtinRes.status);
      if (!skillsRes.ok) loadErrs.push("技能接口 HTTP " + skillsRes.status);
      const builtins = builtinRes.ok ? await builtinRes.json() : [];
      const skills = skillsRes.ok ? await skillsRes.json() : [];

      const builtinCandidates = (Array.isArray(builtins) ? builtins : [])
        .map(t => ({
          group: "Tools",
          label: t.name || t.id || "",
          internal: t.id || t.Id || "",
          desc: t.description || t.Description || ""
        }))
        .filter(c => c.internal);

      const skillCandidates = (Array.isArray(skills) ? skills : [])
        .filter(s => s.enabled !== false && s.Enabled !== false)
        .map(s => {
          const id = s.id || s.Id || "";
          return {
            group: "Skills",
            label: s.name || s.Name || id,
            internal: "skill-progressive:" + id,
            desc: s.description || s.Description || "",
            insertText:
              "（用户技能「" +
              (s.name || s.Name || id) +
              "」Id=" +
              id +
              "：请先调用工具 load_user_skill_instructions 并传入该 skillId 以加载正文，再按需使用 Word/Excel 等业务工具。）"
          };
        });

      // Basic sorting for stable UX
      builtinCandidates.sort((a, b) => String(a.label).localeCompare(String(b.label), "zh-Hans"));
      skillCandidates.sort((a, b) => String(a.label).localeCompare(String(b.label), "zh-Hans"));
      atModeCandidates = [...builtinCandidates, ...skillCandidates];
      atModeLoaded = true;
      if (loadErrs.length) {
        atModeLoadError =
          "部分数据加载失败：" +
          loadErrs.join("；") +
          "。请确认本机后台已启动（" +
          API_BASE +
          "）。" +
          (atModeCandidates.length ? " 以下为已成功加载的条目。" : "");
      }
      if (!atModeCandidates.length) {
        atModeLoadError =
          (loadErrs.length ? atModeLoadError + " " : "") + "当前没有可用的工具或技能可选。";
      }
    } catch (e) {
      console.warn("Failed to load @ mode candidates", e);
      atModeCandidates = [];
      atModeLoaded = true; // don't block UI forever
      atModeLoadError =
        "无法加载工具/技能列表：" +
        (e && e.message ? e.message : String(e)) +
        "。请确认本机后台已启动。";
    }
  })();
  return atModeLoading;
}

let atModeOpen = false;
let atModeActiveIndex = 0;
let atTokenStart = -1; // index of '@'
let atTokenEnd = -1; // caret index at open time (exclusive)

let $atModePanel = null;
let $atModeListEl = null;

function getTextareaCaret() {
  if (!$input) return 0;
  // selectionStart is 0-based caret offset
  return Number($input.selectionStart || 0);
}

function isWhitespace(ch) {
  return /\s/.test(ch);
}

function findAtTokenInTextarea() {
  const value = $input.value || "";
  const caret = getTextareaCaret();
  const left = caret - 1;
  if (left < 0) return null;

  // Scan left until whitespace; keep the closest '@' within this region.
  let i = left;
  let lastAt = -1;
  while (i >= 0) {
    const ch = value[i];
    if (isWhitespace(ch)) break;
    if (ch === "@") lastAt = i;
    i--;
  }
  if (lastAt < 0) return null;

  return {
    atIndex: lastAt,
    caret,
    filter: value.slice(lastAt + 1, caret)
  };
}

function buildAtModeTopList(filterRaw) {
  const filter = (filterRaw || "").trim().toLowerCase();
  const list = atModeCandidates || [];
  if (!list.length) return [];
  const scored = [];
  for (const c of list) {
    const label = String(c.label || "").toLowerCase();
    const internal = String(c.internal || "").toLowerCase();
    const text = `${label} ${internal}`;
    if (filter && !text.includes(filter)) continue;
    let score = 0;
    if (!filter) score = 1;
    else if (label.startsWith(filter) || internal.startsWith(filter)) score = 100;
    else if (label.includes(filter) || internal.includes(filter)) score = 50;
    scored.push({ c, score });
  }
  scored.sort((a, b) => {
    const g1 = a.c.group === "Skills" ? 0 : 1;
    const g2 = b.c.group === "Skills" ? 0 : 1;
    if (g1 !== g2) return g1 - g2;
    if (b.score !== a.score) return b.score - a.score;
    return String(a.c.label).localeCompare(String(b.c.label), "zh-Hans");
  });
  return scored.map(x => x.c);
}

function openAtModeWithTop(top, startIdx, endIdx) {
  if (!$atModePanel || !$atModeListEl) return;
  if (!top.length) return;
  atModeOpen = true;
  atTokenStart = startIdx;
  atTokenEnd = endIdx;
  atModeActiveIndex = 0;
  $atModePanel.style.display = "block";
  renderAtModeListTop(top);
}

function closeAtMode() {
  atModeOpen = false;
  atModeActiveIndex = 0;
  atTokenStart = -1;
  atTokenEnd = -1;
  if ($atModePanel) $atModePanel.style.display = "none";
}

function setAtActiveIndex(idx) {
  const items = $atModeListEl?.querySelectorAll(".at-mode-item");
  if (!items || items.length === 0) return;
  const clamped = Math.max(0, Math.min(items.length - 1, idx));
  atModeActiveIndex = clamped;
  items.forEach((el, i) => {
    if (i === clamped) el.classList.add("at-mode-item--active");
    else el.classList.remove("at-mode-item--active");
  });
  const activeEl = items[clamped];
  if (activeEl && typeof activeEl.scrollIntoView === "function") {
    activeEl.scrollIntoView({ block: "nearest", inline: "nearest" });
  }
}

function escapeHtmlAttr(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;");
}

function findAtCandidateByInternal(internal) {
  if (internal == null || internal === "") return null;
  return atModeCandidates.find((c) => String(c.internal) === String(internal)) || null;
}

function pickActiveAtCandidate() {
  const items = $atModeListEl?.querySelectorAll(".at-mode-item");
  const active = items ? items[atModeActiveIndex] : null;
  const internal = active?.getAttribute("data-internal");
  const c = findAtCandidateByInternal(internal);
  if (c) insertAtCandidate(c);
}

let atModeSyncScheduled = false;
function scheduleAtModeSync() {
  if (atModeSyncScheduled) return;
  atModeSyncScheduled = true;
  queueMicrotask(() => {
    atModeSyncScheduled = false;
    void updateAtModeFromTextarea();
  });
}

function renderAtModeListTop(top) {
  if (!$atModeListEl || !top.length) return;
  atModeActiveIndex = 0;
  const chunks = [];
  for (let idx = 0; idx < top.length; idx++) {
    const c = top[idx];
    if (idx > 0 && c.group === "Tools" && top[idx - 1].group === "Skills") {
      chunks.push('<div class="at-mode-separator" role="separator" aria-hidden="true"></div>');
    }
    const safeLabel = escapeHtml(String(c.label || c.internal || ""));
    const activeCls = idx === atModeActiveIndex ? " at-mode-item--active" : "";
    chunks.push(`
      <div class="at-mode-item${activeCls}" data-at-idx="${idx}" data-internal="${escapeHtmlAttr(c.internal || "")}" role="option" aria-selected="${idx === atModeActiveIndex ? "true" : "false"}">
        <div class="at-mode-item-title">${safeLabel}</div>
      </div>
    `);
  }
  $atModeListEl.innerHTML = chunks.join("");
  const first = $atModeListEl.querySelector(".at-mode-item--active");
  if (first && typeof first.scrollIntoView === "function") {
    first.scrollIntoView({ block: "nearest", inline: "nearest" });
  }
}

function insertAtCandidate(candidate) {
  if (!$input || !$atModePanel) return;
  if (!candidate || atTokenStart < 0 || atTokenEnd < 0) return;

  const value = $input.value || "";
  const internal = candidate.internal || "";
  const inserted =
    candidate.insertText != null && String(candidate.insertText).length > 0
      ? String(candidate.insertText)
      : `[TOOL:${internal}]`;
  const afterChar = value[atTokenEnd] || "";
  const trailing = afterChar && !/\s/.test(afterChar) ? " " : "";

  const newValue = value.slice(0, atTokenStart) + inserted + trailing + value.slice(atTokenEnd);
  $input.value = newValue;

  const newCaret = atTokenStart + inserted.length + trailing.length;
  $input.setSelectionRange(newCaret, newCaret);
  closeAtMode();
  $input.focus();
}

async function updateAtModeFromTextarea() {
  if (!$input) return;
  const token = findAtTokenInTextarea();
  if (!token) {
    if (atModeOpen) closeAtMode();
    return;
  }

  // Allow @ after start or non-ASCII-alnum (e.g. CJK); block typical user@domain (prev is [A-Za-z0-9_]).
  const value = $input.value || "";
  const prev = token.atIndex > 0 ? value[token.atIndex - 1] : "";
  const allow = token.atIndex === 0 || !/[A-Za-z0-9_]/.test(prev);
  if (!allow) {
    if (atModeOpen) closeAtMode();
    return;
  }

  if (!atModeLoaded) await loadAtModeCandidates();

  const filter = token.filter || "";
  const top = buildAtModeTopList(filter);
  if (!top.length) {
    if (atModeOpen) closeAtMode();
    if (!atModeCandidates.length && atModeLoadError) {
      debugLog("AtMode", atModeLoadError, "warn");
    }
    return;
  }

  // 仅按 ↑↓ 时 caret 与 @ 片段未变，不要再次 render（否则会重置高亮）。
  if (atModeOpen && atTokenStart === token.atIndex && atTokenEnd === token.caret) {
    return;
  }
  openAtModeWithTop(top, token.atIndex, token.caret);
}

function initAtModeUI() {
  $atModePanel = document.getElementById("at-mode-panel");
  $atModeListEl = document.getElementById("at-mode-list");
  if (!$atModePanel || !$atModeListEl || !$input) return;

  // Click item to insert
  $atModeListEl.addEventListener("mousedown", (e) => {
    // Prevent textarea losing selection before insert.
    e.preventDefault();
    const item = e.target?.closest?.(".at-mode-item");
    if (!item) return;
    const internal = item.getAttribute("data-internal");
    const candidate = findAtCandidateByInternal(internal);
    if (candidate) insertAtCandidate(candidate);
  });

  $input.addEventListener("keyup", () => {
    void updateAtModeFromTextarea();
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

/** 与后端历史列表 titlePreview 一致：首条用户话截断 80 字（见 SqliteChatSessionStore）。 */
const STORAGE_CONV_TITLE = "copilot_conv_title";
const STORAGE_CONV_TITLE_SID = "copilot_conv_title_sid";
const MAX_CONV_TITLE_CHARS = 80;

function truncConvTitlePreview(text) {
  let t = (text != null ? String(text) : "").trim().replace(/\s+/g, " ");
  if (!t) return "";
  if (t.length <= MAX_CONV_TITLE_CHARS) return t;
  return t.slice(0, MAX_CONV_TITLE_CHARS) + "…";
}

function getConversationTitleForCurrentSession() {
  const id = getSessionId();
  const sid = sessionStorage.getItem(STORAGE_CONV_TITLE_SID);
  if (sid !== id) return "";
  return sessionStorage.getItem(STORAGE_CONV_TITLE) || "";
}

function refreshConversationTitleLabel() {
  const id = getSessionId();
  const t = getConversationTitleForCurrentSession();
  const label = t || "（尚未发送消息）";
  updateCurrentPageLabel(label, id);
}

/** 仅在本会话尚无标题时写入（首条用户消息定标题）。 */
function trySetFirstConversationTitlePreview(userVisibleText) {
  if (getConversationTitleForCurrentSession()) return;
  const truncated = truncConvTitlePreview(userVisibleText);
  if (!truncated) return;
  sessionStorage.setItem(STORAGE_CONV_TITLE_SID, getSessionId());
  sessionStorage.setItem(STORAGE_CONV_TITLE, truncated);
  refreshConversationTitleLabel();
}

function clearConversationTitleStorage() {
  sessionStorage.removeItem(STORAGE_CONV_TITLE);
  sessionStorage.removeItem(STORAGE_CONV_TITLE_SID);
}

/** 从历史 API 回填首条用户话作为顶栏标题（与列表一致）。 */
function applyConversationTitleFromLoadedMessages(msgs) {
  const id = getSessionId();
  let firstUser = "";
  for (let i = 0; i < (msgs || []).length; i++) {
    const m = msgs[i];
    if ((m.role || "").toLowerCase() === "user" && (m.text || "").trim()) {
      firstUser = String(m.text).trim();
      break;
    }
  }
  if (firstUser) {
    sessionStorage.setItem(STORAGE_CONV_TITLE_SID, id);
    sessionStorage.setItem(STORAGE_CONV_TITLE, truncConvTitlePreview(firstUser));
  } else {
    clearConversationTitleStorage();
  }
  refreshConversationTitleLabel();
}

// ───── DevTools logging（侧栏页面右键「检查」打开 Console） ─────
function debugLog(tag, message, type = "info", detail) {
  const prefix = "[OfficeCopilot]";
  const line = `[${tag}] ${message}`;
  if (type === "err") {
    if (detail !== undefined) console.error(prefix, line, detail);
    else console.error(prefix, line);
  } else if (type === "warn") {
    if (detail !== undefined) console.warn(prefix, line, detail);
    else console.warn(prefix, line);
  } else if (type === "send" || type === "recv" || type === "rpc") {
    if (detail !== undefined) console.debug(prefix, line, detail);
    else console.debug(prefix, line);
  } else {
    if (detail !== undefined) console.log(prefix, line, detail);
    else console.log(prefix, line);
  }
}

function escapeHtml(unsafe) {
    return unsafe
         .replace(/&/g, "&amp;")
         .replace(/</g, "&lt;")
         .replace(/>/g, "&gt;")
         .replace(/"/g, "&quot;")
         .replace(/'/g, "&#039;");
}

async function refreshAgentProfileSelector() {
  if (!$agentProfileSelect) return;
  try {
    await tasklyEnsureApiBase();
    await ensureLocalServiceTokenFromBootstrap();
    const res = await tasklyFetch(API_BASE + "/api/config");
    const data = await res.json();
    if (!res.ok) return;
    const list = data.agentProfiles || data.AgentProfiles || [];
    _suppressAgentProfileSelectChange = true;
    $agentProfileSelect.innerHTML = "";
    for (let i = 0; i < list.length; i++) {
      const p = list[i];
      const id = String(p.id || p.Id || "").trim();
      if (!id) continue;
      const opt = document.createElement("option");
      opt.value = id;
      opt.textContent = p.displayName || p.DisplayName || id;
      $agentProfileSelect.appendChild(opt);
    }
    if ($agentProfileSelect.options.length === 0) {
      const opt = document.createElement("option");
      opt.value = "default";
      opt.textContent = "默认助手";
      $agentProfileSelect.appendChild(opt);
    }
    const serverDefault = String(data.activeAgentProfileId || data.ActiveAgentProfileId || "default").trim() || "default";
    await new Promise(function (resolve) {
      chrome.storage.local.get([STORAGE_ACTIVE_AGENT_PROFILE_ID], function (r) {
        let active = String((r && r[STORAGE_ACTIVE_AGENT_PROFILE_ID]) || serverDefault).trim() || serverDefault;
        const ids = Array.from($agentProfileSelect.options).map(function (o) {
          return o.value;
        });
        if (ids.indexOf(active) < 0) active = ids[0] || "default";
        $agentProfileSelect.value = active;
        const o = {};
        o[STORAGE_ACTIVE_AGENT_PROFILE_ID] = active;
        chrome.storage.local.set(o, function () {
          resolve();
        });
      });
    });
  } catch (e) {
    console.warn("refreshAgentProfileSelector", e);
  } finally {
    _suppressAgentProfileSelectChange = false;
  }
}

// ───── WebSocket ─────

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
    return;
  }

  sessionId = getSessionId();
  ensureLocalServiceTokenFromBootstrap().then(function () {
  chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY, STORAGE_ACTIVE_AGENT_PROFILE_ID], function (r) {
    var token = (r && r[COPILOT_TOKEN_STORAGE_KEY] || "").trim();
    var ap = String((r && r[STORAGE_ACTIVE_AGENT_PROFILE_ID]) || "default").trim() || "default";
    var qs = new URLSearchParams();
    qs.set("sessionId", sessionId);
    qs.set("clientType", "chrome");
    qs.set("agentProfileId", ap);
    if (token) qs.set("token", token);
    var url = WS_URL + "?" + qs.toString();
    ws = new WebSocket(url);

  ws.addEventListener("open", async () => {
    reconnectDelay = RECONNECT_BASE_MS;
    reconnectAttempts = 0;
    tasklyLocalServiceWaitHintShown = false;
    setStatus("connected");
    addSystemMessage("已连接到本地服务");
    debugLog("WS", "connected sessionId=" + sessionId, "recv");
    refreshConversationTitleLabel();
    const pageTitle = await getActiveTabTitle();
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
    flushCrossAgentAutoRunAfterReconnect();
    flushPendingMeetingSummaryFromStorage();
  });

  ws.addEventListener("message", (e) => {
    handleMessage(e.data);
  });

  ws.addEventListener("close", () => {
    const wasStreaming = !!currentRoundWrapper;
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
  });
  }).catch(function (err) {
    setStatus("reconnecting");
    if (!tasklyLocalServiceWaitHintShown) {
      tasklyLocalServiceWaitHintShown = true;
      addSystemMessage(
        "未检测到本机 Office Copilot，将自动重试连接。请先启动本机服务端或稍候片刻。\n" +
          formatLocalServiceConnectError(err)
      );
    }
    scheduleReconnect();
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

function send(text, attachmentsPayload = null, sendOptions = {}) {
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    addBotMessage("连接已断开，消息未发送。请检查网络或刷新侧边栏后重试。", true);
    return;
  }
  const skipPlan = sendOptions && sendOptions.skipPlan === true;
  const planId = skipPlan ? undefined : (getCurrentPlanId() || undefined);
  const base = { type: "text", content: text || "", mode: "agent" };
  if (planId) {
    base.planId = planId;
    base.planCurrentStepIndex = getPlanCurrentStepIndex();
  }
  if (attachmentsPayload && attachmentsPayload.length > 0) base.attachments = attachmentsPayload;
  const payload = JSON.stringify(base);
  ws.send(payload);
  debugLog("WS Send", "type=text planId=" + (planId || "-") + " step=" + (planId ? getPlanCurrentStepIndex() : "-") + " len=" + (text || "").length, "send");
}

var MEETING_SUMMARY_STORAGE_KEY = "meetingSummaryPending";

function tasklyMeetingSummaryUserContent(sid, leadIn) {
  var pre = (leadIn || "").trim();
  if (pre && !pre.endsWith("。") && !pre.endsWith(".")) pre += "。";
  return (
    pre +
    "实录已按会话落盘，会话 ID（sessionId）：" + sid + "。\n\n" +
    "请使用 MeetingTranscript 插件：先调用 meeting_transcript_meta 查看 totalChars；再反复调用 meeting_transcript_read，" +
    "参数 sessionId=\"" + sid + "\"、offsetChars 从 0 开始，之后每次使用上一段返回的 nextOffset，直到 hasMore 为 false。\n" +
    "在**读完所有分块**后，生成会议纪要（会议主题、讨论要点、决议、待办事项）。不要猜测未读取的内容。\n\n" +
    "说明：此为正式总结任务；会中侧栏与实录页展示仅为语音转写实录。"
  );
}

function flushPendingMeetingSummaryFromStorage() {
  if (typeof chrome === "undefined" || !chrome.storage || !chrome.storage.local) return;
  chrome.storage.local.get([MEETING_SUMMARY_STORAGE_KEY], function (r) {
    var v = r && r[MEETING_SUMMARY_STORAGE_KEY];
    if (!v || !v.sessionId) return;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    addSystemMessage("正在根据 sessionId=" + v.sessionId + " 生成会议纪要（实录页请求）…");
    send(tasklyMeetingSummaryUserContent(String(v.sessionId), "会议实录页请求生成会议纪要"));
    chrome.storage.local.remove([MEETING_SUMMARY_STORAGE_KEY], function () {});
  });
}

if (typeof chrome !== "undefined" && chrome.storage && chrome.storage.onChanged) {
  chrome.storage.onChanged.addListener(function (changes, area) {
    if (area !== "local" || !changes[MEETING_SUMMARY_STORAGE_KEY]) return;
    var nv = changes[MEETING_SUMMARY_STORAGE_KEY].newValue;
    if (!nv || !nv.sessionId) return;
    setTimeout(flushPendingMeetingSummaryFromStorage, 0);
  });
}

function scheduleCrossAgentAutoRun() {
  if (crossAgentAutoRunLock) {
    crossAgentAutoRunQueued = true;
    return;
  }
  if (currentRoundWrapper) {
    crossAgentAutoRunQueued = true;
    return;
  }
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    crossAgentAutoRunQueued = true;
    return;
  }
  crossAgentAutoRunLock = true;
  crossAgentAutoRunQueued = false;
  addUserMessage(CROSS_AGENT_AUTO_TRIGGER_TEXT);
  showThinkingIndicator();
  send(CROSS_AGENT_AUTO_TRIGGER_TEXT, null, { skipPlan: true });
}

function onCrossAgentTaskPush(msg) {
  const tid = msg && msg.taskId != null ? String(msg.taskId) : "";
  const desc = msg && msg.description != null ? String(msg.description).trim() : "";
  let line = "已收到来自其他端的跨端任务";
  if (tid) line += "（id=" + tid + "）";
  line += "。";
  if (desc) {
    const max = 180;
    line += "摘要：" + (desc.length > max ? desc.slice(0, max) + "…" : desc);
  }
  addSystemMessage(line);
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    crossAgentAutoRunQueued = true;
    addSystemMessage("当前未连接，重连成功后将自动尝试执行跨端待办。");
    return;
  }
  scheduleCrossAgentAutoRun();
}

function flushCrossAgentAutoRunAfterReconnect() {
  if (!crossAgentAutoRunQueued || crossAgentAutoRunLock || currentRoundWrapper) return;
  scheduleCrossAgentAutoRun();
}

// ───── 主题联动（hljs / Mermaid） ─────
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
  if (typeof TasklyTheme === "undefined") return;
  const v = e.newValue != null && e.newValue !== "" ? e.newValue : "dark";
  TasklyTheme.applyThemeDomOnly(v);
  tasklyRefreshEmbedThemes();
});

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

tasklyRefreshEmbedThemes();

// ───── Streaming state ─────

let currentBotMessageRaw = "";
// 本轮回复：时间线（prep / think / intent / digest / tool / subtask）+ 底部完整结论区
let currentRoundWrapper = null;
let timelineRoot = null;
let openPrepSeg = null;
let openThinkSeg = null;
let openDigestSeg = null;
let openIntentSeg = null;
/** 模型正在流式生成工具参数时的展示段（tool_call_delta） */
let openToolDraftSeg = null;
/** 子代理内 tool_call_delta 草稿（挂在 subtask-inner 内、tools 列表上方） */
let openSubtaskToolDraft = null;
/** 工具块 summary 上的耗时定时器（WeakMap: block -> intervalId） */
const toolBlockElapsedTimers = new WeakMap();
/** 当前一段「助手回复」流（工具调用前会关闭并新建，保证与时间线顺序一致） */
let openAnswerSeg = null;
const TIMELINE_TAIL_MAX = 100;
let currentRoundToolBlocks = [];
let currentToolEndIndex = 0;
let currentSubtaskBlock = null;
let currentSubtaskStreamEl = null;
let currentSubtaskToolsEl = null;
let currentSubtaskToolBlocks = [];
let currentSubtaskToolEndIndex = 0;

/** 与服务端 blockSeq 对齐的推理/正文段（Map: seq -> { details, pre|body, tail, rawMd? }） */
let timelineThinkCells = new Map();
let timelineAnswerCells = new Map();
let _reasoningPendingSeq = null;

/** 将带 data-block-seq 的时间线段按序号插入 timelineRoot（无序号子节点视为透明跳过） */
function insertTimelineBlockInOrder(detailsEl, blockSeq) {
  ensureTimeline();
  detailsEl.dataset.blockSeq = String(blockSeq);
  const nodes = timelineRoot.children;
  for (let i = 0; i < nodes.length; i++) {
    const node = nodes[i];
    const raw = node.dataset && node.dataset.blockSeq;
    if (raw == null || raw === "") continue;
    const n = parseInt(raw, 10);
    if (Number.isFinite(n) && n > blockSeq) {
      timelineRoot.insertBefore(detailsEl, node);
      return;
    }
  }
  timelineRoot.appendChild(detailsEl);
}

function collapseThinkSegmentsWithSeqLessThan(answerSeq) {
  if (!timelineRoot) return;
  timelineRoot.querySelectorAll(":scope > .timeline-seg--think").forEach(function (el) {
    const raw = el.dataset.blockSeq;
    if (raw == null || raw === "") return;
    const s = parseInt(raw, 10);
    if (Number.isFinite(s) && s < answerSeq) el.open = false;
  });
}

function ensureThinkTimelineBlock(blockSeq) {
  let cell = timelineThinkCells.get(blockSeq);
  if (cell) return cell;
  ensureTimeline();
  const d = document.createElement("details");
  d.className = "timeline-seg timeline-seg--think";
  d.dataset.kind = "think";
  d.open = true;
  const sum = document.createElement("summary");
  const lab = document.createElement("span");
  lab.className = "timeline-seg__label";
  lab.textContent = "推理";
  const tail = document.createElement("span");
  tail.className = "timeline-seg__tail";
  sum.appendChild(lab);
  sum.appendChild(document.createTextNode(" "));
  sum.appendChild(tail);
  const pre = document.createElement("pre");
  pre.className = "timeline-seg__body";
  d.appendChild(sum);
  d.appendChild(pre);
  insertTimelineBlockInOrder(d, blockSeq);
  cell = { details: d, pre, tail };
  timelineThinkCells.set(blockSeq, cell);
  return cell;
}

function ensureAnswerTimelineBlock(blockSeq) {
  let cell = timelineAnswerCells.get(blockSeq);
  if (cell) return cell;
  ensureTimeline();
  const d = document.createElement("details");
  d.className = "timeline-seg timeline-seg--answer";
  d.dataset.kind = "answer";
  d.open = true;
  const sum = document.createElement("summary");
  const lab = document.createElement("span");
  lab.className = "timeline-seg__label";
  lab.textContent = "助手回复";
  const tail = document.createElement("span");
  tail.className = "timeline-seg__tail";
  sum.appendChild(lab);
  sum.appendChild(document.createTextNode(" "));
  sum.appendChild(tail);
  const body = document.createElement("div");
  body.className = "timeline-seg__body timeline-seg__body--md";
  d.appendChild(sum);
  d.appendChild(body);
  insertTimelineBlockInOrder(d, blockSeq);
  cell = { details: d, body, tail, rawMd: "" };
  timelineAnswerCells.set(blockSeq, cell);
  return cell;
}

/** 工具开始时折叠除「推理」外的阶段，避免误清空 openThinkSeg 导致长时间无可见动态 */
function collapsePhasesForToolStart() {
  collapseSeg(openPrepSeg);
  openPrepSeg = null;
  collapseSeg(openDigestSeg);
  openDigestSeg = null;
  collapseSeg(openIntentSeg);
  openIntentSeg = null;
  closeOpenAnswerSegment();
}

function ensureTimeline() {
  if (!currentRoundWrapper) return;
  if (timelineRoot) return;
  timelineRoot = document.createElement("div");
  timelineRoot.className = "msg msg--agent-timeline";
  timelineRoot.setAttribute("role", "region");
  timelineRoot.setAttribute("aria-label", "助手处理过程");
  currentRoundWrapper.appendChild(timelineRoot);
}

function closeOpenAnswerSegment() {
  if (openAnswerSeg) {
    collapseSeg({ details: openAnswerSeg.details });
    openAnswerSeg = null;
  }
}

/** 时间线内的 Markdown 正文段（与 prep/think/tool 同一列表，按发生顺序排列） */
function newAnswerStreamSeg() {
  ensureTimeline();
  const d = document.createElement("details");
  d.className = "timeline-seg timeline-seg--answer";
  d.dataset.kind = "answer";
  d.open = true;
  const sum = document.createElement("summary");
  const lab = document.createElement("span");
  lab.className = "timeline-seg__label";
  lab.textContent = "助手回复";
  const tail = document.createElement("span");
  tail.className = "timeline-seg__tail";
  sum.appendChild(lab);
  sum.appendChild(document.createTextNode(" "));
  sum.appendChild(tail);
  const body = document.createElement("div");
  body.className = "timeline-seg__body timeline-seg__body--md";
  d.appendChild(sum);
  d.appendChild(body);
  timelineRoot.appendChild(d);
  return { details: d, body, tail, rawMd: "" };
}

function runMermaidInTimeline(root) {
  if (!root || typeof mermaid === "undefined") return;
  root.querySelectorAll(".timeline-seg--answer .language-mermaid").forEach((block, index) => {
    const id = `mermaid-${Date.now()}-${index}`;
    const code = block.textContent;
    const container = document.createElement("div");
    container.className = "mermaid-container";
    container.id = id;
    block.parentNode.replaceWith(container);
    mermaid.render(id + "-svg", code).then((result) => {
      container.innerHTML = result.svg;
    }).catch((err) => {
      container.innerHTML = `<pre>Mermaid Error: ${err.message}</pre>`;
    });
  });
}

function timelineTail(s, max) {
  const t = s || "";
  if (t.length <= max) return t;
  return "…" + t.slice(t.length - max);
}

/** 主时间线里第一个「执行中」的工具块；新建推理段应插在它前面，否则会跑到工具块下方，看起来像「意图→工具」之间没有推理 */
function getTimelineInsertBeforeForLiveThink() {
  if (!timelineRoot) return null;
  return timelineRoot.querySelector(":scope > .tool-call-block.tool-call--running");
}

/** @param {Node | null | undefined} insertBeforeNode 若为 timelineRoot 的直接子节点，则插在它之前；否则追加到末尾 */
function newTimelineSeg(kind, titleLabel, insertBeforeNode) {
  ensureTimeline();
  const d = document.createElement("details");
  d.className = "timeline-seg timeline-seg--" + kind;
  d.dataset.kind = kind;
  d.open = true;
  const sum = document.createElement("summary");
  const lab = document.createElement("span");
  lab.className = "timeline-seg__label";
  lab.textContent = titleLabel;
  const tail = document.createElement("span");
  tail.className = "timeline-seg__tail";
  sum.appendChild(lab);
  sum.appendChild(document.createTextNode(" "));
  sum.appendChild(tail);
  const pre = document.createElement("pre");
  pre.className = "timeline-seg__body";
  d.appendChild(sum);
  d.appendChild(pre);
  if (insertBeforeNode && insertBeforeNode.parentNode === timelineRoot) {
    timelineRoot.insertBefore(d, insertBeforeNode);
  } else {
    timelineRoot.appendChild(d);
  }
  return { details: d, pre, tail };
}

function collapseSeg(ref) {
  if (ref && ref.details) ref.details.open = false;
}

function collapseAllOpenPhases() {
  flushReasoningPendingSync();
  collapseSeg(openPrepSeg);
  openPrepSeg = null;
  if (timelineRoot) {
    timelineRoot.querySelectorAll(":scope > .timeline-seg--think").forEach(function (el) {
      el.open = false;
    });
  }
  collapseSeg(openThinkSeg);
  openThinkSeg = null;
  collapseSeg(openDigestSeg);
  openDigestSeg = null;
  collapseSeg(openIntentSeg);
  openIntentSeg = null;
  closeOpenAnswerSegment();
}

function clearToolDraftTimeline() {
  if (openToolDraftSeg && openToolDraftSeg.details && openToolDraftSeg.details.parentNode) {
    openToolDraftSeg.details.remove();
  }
  openToolDraftSeg = null;
}

function clearSubtaskToolDraft() {
  if (openSubtaskToolDraft && openSubtaskToolDraft.wrap && openSubtaskToolDraft.wrap.parentNode) {
    openSubtaskToolDraft.wrap.remove();
  }
  openSubtaskToolDraft = null;
}

function ensureSubtaskToolDraft() {
  if (openSubtaskToolDraft) return openSubtaskToolDraft;
  if (!currentSubtaskToolsEl || !currentSubtaskToolsEl.parentNode) return null;
  const inner = currentSubtaskToolsEl.parentNode;
  const wrap = document.createElement("details");
  wrap.className = "subtask-tool-draft-wrap";
  wrap.open = true;
  const sum = document.createElement("summary");
  sum.textContent = "工具参数（生成中）";
  const pre = document.createElement("pre");
  pre.className = "subtask-tool-draft-body";
  wrap.appendChild(sum);
  wrap.appendChild(pre);
  inner.insertBefore(wrap, currentSubtaskToolsEl);
  openSubtaskToolDraft = { wrap, pre, lastCallId: "" };
  return openSubtaskToolDraft;
}

function ensureToolDraftSeg() {
  if (openToolDraftSeg) return openToolDraftSeg;
  if (!currentRoundWrapper) beginStream();
  ensureTimeline();
  const seg = newTimelineSeg("tool-draft", "工具参数（生成中）");
  seg.details.classList.add("timeline-seg--tool-draft");
  openToolDraftSeg = { details: seg.details, pre: seg.pre, tail: seg.tail, lastCallId: "" };
  return openToolDraftSeg;
}

function appendToolCallDelta(msg) {
  const callIdRaw = msg.toolCallId != null ? String(msg.toolCallId).trim() : "";
  const callId = callIdRaw || "_";
  const name = msg.toolName != null ? String(msg.toolName) : "";
  const delta = msg.argumentsDelta != null ? String(msg.argumentsDelta) : "";
  if (!delta && !name.trim()) return;
  if (msg.isSubtask === true) {
    if (!currentRoundWrapper) beginStream();
    if (!currentSubtaskToolsEl) return;
    const sd = ensureSubtaskToolDraft();
    if (!sd) return;
    if (sd.lastCallId !== callId) {
      if (sd.pre.textContent) sd.pre.textContent += "\n\n";
      sd.pre.textContent += "[" + callId + "]" + (name.trim() ? " " + name.trim() : "") + "\n";
      sd.lastCallId = callId;
    }
    sd.pre.textContent += delta;
    if ($messages) $messages.scrollTop = $messages.scrollHeight;
    return;
  }
  if (!currentRoundWrapper) beginStream();
  ensureTimeline();
  const d = ensureToolDraftSeg();
  if (d.lastCallId !== callId) {
    if (d.pre.textContent) d.pre.textContent += "\n\n";
    d.pre.textContent += "[" + callId + "]" + (name.trim() ? " " + name.trim() : "") + "\n";
    d.lastCallId = callId;
  }
  d.pre.textContent += delta;
  d.tail.textContent = timelineTail(d.pre.textContent, TIMELINE_TAIL_MAX);
  d.details.title = (d.pre.textContent || "").slice(0, 500);
  if ($messages) $messages.scrollTop = $messages.scrollHeight;
}

function startToolElapsedTimer(block) {
  if (!block) return;
  const sum = block.querySelector("summary");
  if (!sum) return;
  const out = block.querySelector(".tool-call-output");
  const span = document.createElement("span");
  span.className = "tool-elapsed";
  sum.appendChild(span);
  const t0 = Date.now();
  const id = setInterval(function () {
    const s = Math.floor((Date.now() - t0) / 1000);
    span.textContent = " · 已执行 " + s + "s";
    if (out) out.textContent = "执行中，请稍候… 已耗时 " + s + "s";
  }, 1000);
  toolBlockElapsedTimers.set(block, id);
}

function clearToolElapsedTimer(block) {
  if (!block) return;
  const id = toolBlockElapsedTimers.get(block);
  if (id) clearInterval(id);
  toolBlockElapsedTimers.delete(block);
}

function clearAllRunningToolTimers() {
  const wrap = currentRoundWrapper;
  if (!wrap) return;
  wrap.querySelectorAll(".tool-call-block.tool-call--running").forEach(function (b) {
    clearToolElapsedTimer(b);
  });
}

function appendAgentStatusLine(text) {
  const line = (text && String(text).trim()) || "";
  if (!line) return;
  if (!currentRoundWrapper) beginStream();
  if (!openPrepSeg) openPrepSeg = newTimelineSeg("prep", "准备 / 状态");
  const pre = openPrepSeg.pre;
  if (pre.textContent) pre.textContent += "\n";
  pre.textContent += line;
  openPrepSeg.tail.textContent = timelineTail(pre.textContent, TIMELINE_TAIL_MAX);
  openPrepSeg.details.title = pre.textContent;
  if ($messages) $messages.scrollTop = $messages.scrollHeight;
}

/** agent_trace：类目 + 标题 + 多行详情，并入「准备 / 状态」时间线 */
function appendAgentTrace(msg) {
  const title = ((msg.traceTitle && String(msg.traceTitle).trim()) || (msg.content && String(msg.content).trim()) || "");
  const detail = (msg.traceDetail && String(msg.traceDetail).trim()) || "";
  const cat = (msg.traceCategory && String(msg.traceCategory).trim()) || "trace";
  if (!title && !detail) return;
  let block = "[" + cat + "] " + (title || "(无标题)");
  if (detail) block += "\n" + detail;
  appendAgentStatusLine(block);
}

/** 推理流与正文流同理：合并到 rAF，避免每条 reasoning_chunk 都触发布局 + scroll 造成卡顿 */
let _reasoningPendingText = "";
let _reasoningFlushPending = false;
let _reasoningRafId = null;

function cancelReasoningRaf() {
  if (_reasoningRafId != null) {
    cancelAnimationFrame(_reasoningRafId);
    _reasoningRafId = null;
  }
  _reasoningFlushPending = false;
}

function flushReasoningPendingToDom() {
  _reasoningFlushPending = false;
  _reasoningRafId = null;
  const buf = _reasoningPendingText;
  _reasoningPendingText = "";
  if (!buf || !openThinkSeg) return;
  openThinkSeg.pre.textContent += buf;
  openThinkSeg.tail.textContent = timelineTail(openThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
  openThinkSeg.details.title = openThinkSeg.pre.textContent;
  if ($messages) $messages.scrollTop = $messages.scrollHeight;
}

function scheduleReasoningFlush() {
  if (_reasoningFlushPending) return;
  _reasoningFlushPending = true;
  _reasoningRafId = requestAnimationFrame(flushReasoningPendingToDom);
}

/** 折叠/切正文前必须把缓冲写入节点，避免丢字 */
function flushReasoningPendingSync() {
  cancelReasoningRaf();
  if (_reasoningPendingText && openThinkSeg) {
    openThinkSeg.pre.textContent += _reasoningPendingText;
    _reasoningPendingText = "";
    openThinkSeg.tail.textContent = timelineTail(openThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
    openThinkSeg.details.title = openThinkSeg.pre.textContent;
  } else {
    _reasoningPendingText = "";
  }
}

/** 新建「推理」段时应插在何处：优先工具执行中块前；否则插在「当前/最后一段助手回复」之前，避免迟到的 reasoning_chunk 被 append 到时间线末尾、看起来卡在已完成正文之后。 */
function getTimelineInsertBeforeForNewThinkSeg() {
  const liveTool = getTimelineInsertBeforeForLiveThink();
  if (liveTool) return liveTool;
  if (openAnswerSeg && openAnswerSeg.details && openAnswerSeg.details.parentNode === timelineRoot)
    return openAnswerSeg.details;
  if (timelineRoot) {
    const answers = timelineRoot.querySelectorAll(":scope > .timeline-seg--answer");
    if (answers.length) return answers[answers.length - 1];
  }
  return null;
}

function appendReasoningChunk(text, blockSeq, blockKind) {
  const t = text != null ? String(text) : "";
  if (!t) return;
  if (!currentRoundWrapper) beginStream();
  const useBlock =
    typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "think";
  if (useBlock) {
    if (_reasoningPendingSeq !== null && _reasoningPendingSeq !== blockSeq) flushReasoningPendingSync();
    const cell = ensureThinkTimelineBlock(blockSeq);
    openThinkSeg = cell;
    _reasoningPendingSeq = blockSeq;
    _reasoningPendingText += t;
    scheduleReasoningFlush();
    return;
  }
  _reasoningPendingSeq = null;
  if (!openThinkSeg) {
    ensureTimeline();
    openThinkSeg = newTimelineSeg("think", "推理", getTimelineInsertBeforeForNewThinkSeg());
  }
  _reasoningPendingText += t;
  scheduleReasoningFlush();
}

function beginStream() {
  cancelReasoningRaf();
  _reasoningPendingText = "";
  removeThinkingIndicator();
  clearSubtaskToolDraft();
  const welcome = $messages.querySelector(".welcome");
  if (welcome) welcome.remove();

  currentRoundWrapper = document.createElement("div");
  currentRoundWrapper.className = "msg msg--round";
  timelineRoot = null;
  openPrepSeg = null;
  openThinkSeg = null;
  openDigestSeg = null;
  openIntentSeg = null;
  openToolDraftSeg = null;
  openAnswerSeg = null;
  timelineThinkCells = new Map();
  timelineAnswerCells = new Map();
  _reasoningPendingSeq = null;

  $messages.appendChild(currentRoundWrapper);
  currentBotMessageRaw = "";
  currentRoundToolBlocks = [];
  currentToolEndIndex = 0;
  setInputEnabled(false);
}

function updateExecutionLogCount() {
  /* 工具块已直接挂在时间线，无需单独计数 summary */
}

function appendStreamWarning(text) {
  if (!currentRoundWrapper) beginStream();
  ensureTimeline();
  const line = (text && String(text).trim()) || "服务端返回了警告";
  const seg = newTimelineSeg("stream-warning", "服务端提示");
  seg.pre.textContent = line;
  seg.tail.textContent = timelineTail(line, TIMELINE_TAIL_MAX);
  seg.details.title = line;
  if ($messages) $messages.scrollTop = $messages.scrollHeight;
}

let _streamRenderPending = false;
let _streamRenderRafId = null;

/** Markdown 渲染失败时回退为纯文本，避免流式未闭合语法导致整段不显示 */
function applyMarkedToElement(el, rawMarkdown) {
  if (!el) return;
  const raw = rawMarkdown != null ? String(rawMarkdown) : "";
  if (typeof marked === "undefined") {
    el.textContent = raw;
    return;
  }
  try {
    el.innerHTML = marked.parse(raw);
  } catch (e) {
    console.warn("marked.parse failed, using plain text", e);
    el.textContent = raw;
  }
}

function appendStreamChunk(text, blockSeq, blockKind) {
  if (!currentRoundWrapper) beginStream();
  const chunk = text != null ? String(text) : "";
  const useBlock =
    typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "answer";

  if (useBlock) {
    if (!chunk) return;
    flushReasoningPendingSync();
    _reasoningPendingSeq = null;
    collapseThinkSegmentsWithSeqLessThan(blockSeq);
    openThinkSeg = null;
    collapseSeg(openDigestSeg);
    openDigestSeg = null;
    currentBotMessageRaw += chunk;
    const cell = ensureAnswerTimelineBlock(blockSeq);
    openAnswerSeg = cell;
    cell.rawMd += chunk;
    cell.details.dataset.streamRaw = cell.rawMd;

    if (_streamRenderPending) return;
    _streamRenderPending = true;
    _streamRenderRafId = requestAnimationFrame(() => {
      _streamRenderPending = false;
      _streamRenderRafId = null;
      if (!openAnswerSeg) return;
      applyMarkedToElement(openAnswerSeg.body, openAnswerSeg.rawMd);
      const plain = openAnswerSeg.rawMd.replace(/\s+/g, " ").trim();
      openAnswerSeg.tail.textContent = timelineTail(plain, TIMELINE_TAIL_MAX);
      openAnswerSeg.details.title = plain.slice(0, 200);
      if ($messages) $messages.scrollTop = $messages.scrollHeight;
    });
    return;
  }

  if (!chunk) return;
  flushReasoningPendingSync();
  _reasoningPendingSeq = null;
  collapseSeg(openThinkSeg);
  openThinkSeg = null;
  collapseSeg(openDigestSeg);
  openDigestSeg = null;
  currentBotMessageRaw += chunk;
  if (!openAnswerSeg) openAnswerSeg = newAnswerStreamSeg();
  openAnswerSeg.rawMd += chunk;
  openAnswerSeg.details.dataset.streamRaw = openAnswerSeg.rawMd;

  if (_streamRenderPending) return;
  _streamRenderPending = true;
  _streamRenderRafId = requestAnimationFrame(() => {
    _streamRenderPending = false;
    _streamRenderRafId = null;
    if (!openAnswerSeg) return;
    applyMarkedToElement(openAnswerSeg.body, openAnswerSeg.rawMd);
    const plain = openAnswerSeg.rawMd.replace(/\s+/g, " ").trim();
    openAnswerSeg.tail.textContent = timelineTail(plain, TIMELINE_TAIL_MAX);
    openAnswerSeg.details.title = plain.slice(0, 200);
    if ($messages) $messages.scrollTop = $messages.scrollHeight;
  });
}

function finalizeStream() {
  stopHitlWaitTimer();
  if (pendingConfirmId) {
    pendingConfirmId = null;
    if ($hitlHumanSummary) {
      $hitlHumanSummary.textContent = "";
      $hitlHumanSummary.style.display = "none";
    }
    if ($hitlRawLabel) $hitlRawLabel.style.display = "none";
    if ($hitlOverlay) {
      $hitlOverlay.style.display = "none";
      $hitlOverlay.setAttribute("aria-hidden", "true");
    }
  }
  const wrap = currentRoundWrapper;
  clearToolDraftTimeline();
  clearSubtaskToolDraft();
  clearAllRunningToolTimers();
  collapseAllOpenPhases();
  openAnswerSeg = null;
  if (timelineRoot) {
    timelineRoot.querySelectorAll(".timeline-seg--answer").forEach(function (el) {
      const div = el.querySelector(".timeline-seg__body--md");
      const raw = el.dataset.streamRaw;
      if (div && raw != null && typeof marked !== "undefined") {
        applyMarkedToElement(div, raw);
      }
    });
    if (typeof mermaid !== "undefined") runMermaidInTimeline(timelineRoot);
    timelineRoot.querySelectorAll("details").forEach(function (el) {
      el.open = false;
    });
    const answerDetails = timelineRoot.querySelectorAll(".timeline-seg.timeline-seg--answer");
    if (answerDetails.length) answerDetails[answerDetails.length - 1].open = true;
    try {
      const segs = [];
      timelineRoot.querySelectorAll(".timeline-seg").forEach(function (el) {
        const kind = el.dataset.kind || "";
        const raw = el.dataset.streamRaw;
        const pre = el.querySelector("pre.timeline-seg__body");
        const divMd = el.querySelector(".timeline-seg__body--md");
        let t = "";
        if (raw != null && String(raw).length) t = String(raw);
        else if (pre) t = pre.textContent || "";
        else if (divMd) t = divMd.innerText || "";
        segs.push({ kind, text: t });
      });
      if (wrap && segs.length) wrap.dataset.timelineSegments = JSON.stringify(segs);
    } catch (_) { /* ignore */ }
  }

  currentBotMessageRaw = "";
  if (currentRoundToolBlocks.length > 0) {
    currentRoundToolBlocks.forEach(function (b) { b.open = false; });
  }
  currentRoundWrapper = null;
  timelineRoot = null;
  openToolDraftSeg = null;
  openSubtaskToolDraft = null;
  currentRoundToolBlocks = [];
  currentToolEndIndex = 0;
  currentSubtaskBlock = null;
  currentSubtaskStreamEl = null;
  currentSubtaskToolsEl = null;
  currentSubtaskToolBlocks = [];
  currentSubtaskToolEndIndex = 0;
  setInputEnabled(true);
  if (crossAgentAutoRunLock) {
    crossAgentAutoRunLock = false;
  }
  if (crossAgentAutoRunQueued && !currentRoundWrapper) {
    crossAgentAutoRunQueued = false;
    scheduleCrossAgentAutoRun();
  }
}

// ───── 历史对话（/api/chat-sessions）─────
let historySkip = 0;
let historyHasMore = true;
let historyLoading = false;

function showHistoryError(text) {
  if (!$historyError) return;
  const t = (text || "").trim();
  if (!t) {
    $historyError.style.display = "none";
    $historyError.textContent = "";
    return;
  }
  $historyError.textContent = t;
  $historyError.style.display = "block";
}

function updateHistoryLoadMoreButton() {
  if (!$historyLoadMore) return;
  if (!historyHasMore) {
    $historyLoadMore.textContent = "没有更多了";
    $historyLoadMore.disabled = true;
  } else {
    $historyLoadMore.textContent = "加载更多";
    $historyLoadMore.disabled = historyLoading;
  }
}

function closeHistoryOverlay() {
  if (!$historyOverlay) return;
  $historyOverlay.style.display = "none";
  $historyOverlay.setAttribute("aria-hidden", "true");
  showHistoryError("");
}

async function fetchHistoryPage(append) {
  if (historyLoading) return;
  if (append && !historyHasMore) return;
  historyLoading = true;
  if ($historyLoadMore) $historyLoadMore.disabled = true;
  try {
    await tasklyEnsureApiBase();
    await ensureLocalServiceTokenFromBootstrap();
    const skip = append ? historySkip : 0;
    if (!append) {
      historySkip = 0;
      historyHasMore = true;
      if ($historyList) $historyList.innerHTML = "";
    }
    const ap = await new Promise(function (resolve) {
      chrome.storage.local.get([STORAGE_ACTIVE_AGENT_PROFILE_ID], function (r) {
        resolve(String((r && r[STORAGE_ACTIVE_AGENT_PROFILE_ID]) || "default").trim() || "default");
      });
    });
    const res = await tasklyFetch(
      API_BASE + "/api/chat-sessions?skip=" + skip + "&take=10&agentProfileId=" + encodeURIComponent(ap)
    );
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      throw new Error(data.message || "加载历史列表失败");
    }
    const items = data.items || [];
    historyHasMore = !!data.hasMore;
    historySkip = skip + items.length;
    const curSid = getSessionId();
    for (const it of items) {
      appendHistoryListItem(it, curSid);
    }
  } catch (e) {
    showHistoryError(e.message || String(e));
  } finally {
    historyLoading = false;
    updateHistoryLoadMoreButton();
  }
}

function appendHistoryListItem(it, currentSessionId) {
  if (!$historyList || !it || !it.sessionId) return;
  const li = document.createElement("li");
  li.className = "history-list-item" + (it.sessionId === currentSessionId ? " history-list-item--current" : "");
  li.dataset.sessionId = it.sessionId;
  if (it.agentProfileId != null && String(it.agentProfileId).trim() !== "")
    li.dataset.agentProfileId = String(it.agentProfileId).trim();

  const main = document.createElement("div");
  main.className = "history-list-item-main";
  const titleEl = document.createElement("div");
  titleEl.className = "history-list-item-title";
  titleEl.textContent = (it.titlePreview && String(it.titlePreview).trim()) || it.sessionId || "（无标题）";
  const meta = document.createElement("div");
  meta.className = "history-list-item-meta";
  if (it.updatedAtUtc) {
    try {
      meta.textContent = new Date(it.updatedAtUtc).toLocaleString();
    } catch {
      meta.textContent = "";
    }
  }
  main.appendChild(titleEl);
  main.appendChild(meta);

  const delBtn = document.createElement("button");
  delBtn.type = "button";
  delBtn.className = "history-list-item-delete";
  delBtn.textContent = "删除";
  delBtn.title = "删除此历史对话";
  delBtn.addEventListener("click", function (ev) {
    ev.stopPropagation();
    void deleteHistorySession(it.sessionId, li);
  });

  li.addEventListener("click", function () {
    void switchToHistorySession(it.sessionId, it.agentProfileId);
  });

  li.appendChild(main);
  li.appendChild(delBtn);
  $historyList.appendChild(li);
}

async function deleteHistorySession(sid, liEl) {
  if (!sid) return;
  if (!confirm("确定删除此历史对话？本地保存的记录将移除，且无法恢复。")) return;
  try {
    await tasklyEnsureApiBase();
    await ensureLocalServiceTokenFromBootstrap();
    const res = await tasklyFetch(API_BASE + "/api/chat-sessions/" + encodeURIComponent(sid), { method: "DELETE" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      alert(data.message || "删除失败");
      return;
    }
    if (liEl && liEl.parentNode) liEl.parentNode.removeChild(liEl);
    const cur = getSessionId();
    if (cur === sid) {
      sessionStorage.removeItem("copilot_session_id");
      clearConversationTitleStorage();
      setCurrentPlan(null, null);
      if ($messages) $messages.innerHTML = WELCOME_INNER_HTML_NEW_CHAT;
      attachments.length = 0;
      if ($attachmentsPreview) {
        $attachmentsPreview.innerHTML = "";
        $attachmentsPreview.style.display = "none";
      }
      if (ws) {
        ws.close();
        ws = null;
      }
      connect();
    }
  } catch (e) {
    alert(e.message || String(e));
  }
}

async function switchToHistorySession(sid, agentProfileIdFromItem) {
  if (!sid) return;
  finalizeStream();
  try {
    await tasklyEnsureApiBase();
    await ensureLocalServiceTokenFromBootstrap();
    if (agentProfileIdFromItem != null && String(agentProfileIdFromItem).trim() !== "") {
      const ap = String(agentProfileIdFromItem).trim();
      _suppressAgentProfileSelectChange = true;
      try {
        await new Promise(function (resolve) {
          const o = {};
          o[STORAGE_ACTIVE_AGENT_PROFILE_ID] = ap;
          chrome.storage.local.set(o, function () {
            resolve();
          });
        });
        if ($agentProfileSelect) {
          const ids = Array.from($agentProfileSelect.options).map(function (o) {
            return o.value;
          });
          if (ids.indexOf(ap) >= 0) $agentProfileSelect.value = ap;
        }
      } finally {
        _suppressAgentProfileSelectChange = false;
      }
    }
    const res = await tasklyFetch(API_BASE + "/api/chat-sessions/" + encodeURIComponent(sid) + "/messages");
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      addBotMessage(data.message || "加载该对话消息失败", true);
      return;
    }
    sessionStorage.setItem("copilot_session_id", sid);
    setCurrentPlan(null, null);
    attachments.length = 0;
    if ($attachmentsPreview) {
      $attachmentsPreview.innerHTML = "";
      $attachmentsPreview.style.display = "none";
    }
    if ($messages) $messages.innerHTML = "";
    const msgs = data.messages || [];
    for (let i = 0; i < msgs.length; i++) {
      const m = msgs[i];
      const r = (m.role || "").toLowerCase();
      if (r === "user") addUserMessage(m.text || "");
      else addBotMessage(m.text || "", false);
    }
    if (msgs.length === 0 && $messages) {
      $messages.innerHTML = WELCOME_INNER_HTML_NEW_CHAT;
    }
    applyConversationTitleFromLoadedMessages(msgs);
    closeHistoryOverlay();
    if (ws) {
      ws.close();
      ws = null;
    }
    connect();
  } catch (e) {
    addBotMessage(e.message || String(e), true);
  }
}

async function openHistoryOverlay() {
  if (!$historyOverlay) return;
  showHistoryError("");
  historySkip = 0;
  historyHasMore = true;
  if ($historyList) $historyList.innerHTML = "";
  $historyOverlay.style.display = "flex";
  $historyOverlay.setAttribute("aria-hidden", "false");
  updateHistoryLoadMoreButton();
  try {
    await fetchHistoryPage(false);
  } catch (e) {
    showHistoryError(e.message || String(e));
  }
}

if ($historyChatBtn) {
  $historyChatBtn.addEventListener("click", function () {
    void openHistoryOverlay();
  });
}
if ($historyOverlayClose) {
  $historyOverlayClose.addEventListener("click", function () {
    closeHistoryOverlay();
  });
}
if ($historyOverlayBackdrop) {
  $historyOverlayBackdrop.addEventListener("click", function () {
    closeHistoryOverlay();
  });
}
if ($historyLoadMore) {
  $historyLoadMore.addEventListener("click", function () {
    void fetchHistoryPage(true);
  });
}

document.addEventListener("keydown", function (ev) {
  if (ev.key !== "Escape") return;
  if (!$historyOverlay || $historyOverlay.style.display === "none") return;
  if ($historyOverlay.getAttribute("aria-hidden") !== "false") return;
  closeHistoryOverlay();
});

// ───── Message handling ─────
// 主会话「一问一答」中，助手侧一轮 = msg--round：内含 msg--agent-timeline（时间线）与流式块。
// 已进时间线的 WS：stream_start→空壳；reasoning_chunk→推理；tool_call_delta→工具参数草稿；agent_status/agent_trace→准备/状态；
// agent_phase→计划·意图 / 处理工具结果；stream_chunk→助手回复；stream_warning→服务端提示；subtask_* / tool_invocation_*→子任务与工具块。
// 未进时间线（刻意分栏）：用户气泡 msg--user；echo/text/error→msg--bot；plan_* / cross_agent→msg--system；confirm_request / ask_options→遮罩；
// rpc_request→后台执行；pong / ui_theme_changed 非对话内容。

function handleMessage(raw) {
  let msg;
  try {
    msg = JSON.parse(raw);
  } catch {
    msg = { type: "text", content: raw };
  }
  debugLog(
    "WS Recv",
    "message",
    "recv",
    {
      type: msg.type || "?",
      contentLen: typeof msg.content === "string" ? msg.content.length : 0,
    }
  );

  switch (msg.type) {
    case "stream_start":
      beginStream();
      break;

    case "tool_call_delta":
      appendToolCallDelta(msg);
      break;

    case "agent_status": {
      const line = (msg.content && String(msg.content).trim()) || "";
      if (line) appendAgentStatusLine(line);
      break;
    }

    case "agent_trace":
      appendAgentTrace(msg);
      break;

    case "reasoning_chunk":
      appendReasoningChunk(msg.content, msg.blockSeq, msg.blockKind);
      break;

    case "agent_phase": {
      const phase = (msg.phase && String(msg.phase)) || "";
      const c = (msg.content && String(msg.content).trim()) || "";
      if (!c) break;
      if (!currentRoundWrapper) beginStream();
      if (phase === "intent") {
        collapseAllOpenPhases();
        openIntentSeg = newTimelineSeg("intent", "计划 / 意图");
        openIntentSeg.pre.textContent = c;
        openIntentSeg.tail.textContent = timelineTail(c, TIMELINE_TAIL_MAX);
      } else if (phase === "digest") {
        collapseSeg(openDigestSeg);
        openDigestSeg = null;
        openDigestSeg = newTimelineSeg("digest", "处理工具结果");
        openDigestSeg.pre.textContent = c;
        openDigestSeg.tail.textContent = timelineTail(c, TIMELINE_TAIL_MAX);
      }
      if ($messages) $messages.scrollTop = $messages.scrollHeight;
      break;
    }

    case "stream_chunk":
      appendStreamChunk(msg.content, msg.blockSeq, msg.blockKind);
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
      if (!currentRoundWrapper) beginStream();
      clearSubtaskToolDraft();
      ensureTimeline();
      if (!timelineRoot) break;
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
      timelineRoot.appendChild(block);
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
      clearSubtaskToolDraft();
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
      if (msg.isSubtask === true) clearSubtaskToolDraft();
      else clearToolDraftTimeline();
      if (msg.plugin === "Plan" && msg.function === "execute_plan_step" && msg.planStepIndex) {
        const planId = getCurrentPlanId();
        if (planId) {
          ensurePlanChecklistLoaded(planId).then(() => updateChecklistStep(msg.planStepIndex, "in_progress"));
        }
      }
      const label = msg.summary || `正在执行: ${msg.plugin || ""}.${msg.function || ""}`;
      const isSubtask = msg.isSubtask === true;
      collapsePhasesForToolStart();
      ensureTimeline();
      const parentBody = isSubtask ? currentSubtaskToolsEl : timelineRoot;
      if (!parentBody) break;
      const block = document.createElement("details");
      block.className = "tool-call-block tool-call--running" + (isSubtask ? " subtask-tool-block" : "");
      block.dataset.label = label;
      const sum = document.createElement("summary");
      sum.innerHTML = `<span class="tool-status-icon">⏳</span> ${escapeHtml(label)}`;
      block.appendChild(sum);
      const out = document.createElement("pre");
      out.className = "tool-call-output";
      out.textContent = "执行中，请稍候…";
      out.style.display = "block";
      block.appendChild(out);
      parentBody.appendChild(block);
      startToolElapsedTimer(block);
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
        clearToolElapsedTimer(block);
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
            btn.addEventListener("click", () => { openTasklyOptionsPage(); });
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

    case "ui_theme_changed": {
      const tid = (msg.uiThemeId && String(msg.uiThemeId).trim()) || "";
      if (tid && typeof TasklyTheme !== "undefined") {
        TasklyTheme.setTheme(tid);
        if (typeof tasklyRefreshEmbedThemes === "function") tasklyRefreshEmbedThemes();
      }
      break;
    }

    case "error":
      removeThinkingIndicator();
      finalizeStream();
      addBotMessage((msg.content && String(msg.content).trim()) || "请求失败，请稍后重试", true);
      break;

    case "rpc_request":
      handleRpcRequest(msg);
      break;

    case "confirm_request":
      handleConfirmRequest(msg);
      break;

    case "ask_options_request":
      handleAskOptionsRequest(msg);
      break;

    case "plan_created": {
      const planId = msg.planId || "";
      const title = msg.title || "新计划";
      if (planId) {
        setCurrentPlan(planId, title);
        appendPlanCreatedMessage(planId, title);
        chrome.tabs.create({ url: chrome.runtime.getURL("plans.html?id=" + encodeURIComponent(planId)) });
        const welcome = $messages.querySelector(".welcome");
        if (welcome) welcome.remove();
        $messages.scrollTop = $messages.scrollHeight;
      }
      break;
    }

    case "plan_updated": {
      const planId = msg.planId || "";
      const title = msg.title || planId || "计划";
      if (planId) {
        if (getCurrentPlanId() === planId) {
          sessionStorage.setItem(STORAGE_PLAN_TITLE, title);
          if ($currentPlanLabel) {
            $currentPlanLabel.textContent = title;
            $currentPlanLabel.title = "点击查看计划: " + title;
          }
          if (typeof chrome !== "undefined" && chrome.storage?.local) {
            chrome.storage.local.set({ copilot_plan_id: planId, copilot_plan_title: title });
          }
          planChecklistLoadedPlanId = null;
          planChecklistSteps = [];
          planChecklistStatus = {};
          ensurePlanChecklistLoaded(planId);
        }
        appendPlanUpdatedMessage(planId, title);
        reloadOpenPlanTabsForPlanId(planId);
        const welcome = $messages.querySelector(".welcome");
        if (welcome) welcome.remove();
        $messages.scrollTop = $messages.scrollHeight;
      }
      break;
    }

    case "cross_agent_task":
      onCrossAgentTaskPush(msg);
      break;

    case "cross_agent_task_completed": {
      const st = (msg.status && String(msg.status)) || "";
      const rs = (msg.resultSummary && String(msg.resultSummary).trim()) || "";
      const tid = msg.taskId != null ? String(msg.taskId) : "";
      let line = "跨端任务已由对方处理" + (tid ? "（id=" + tid + "）" : "");
      if (st) line += "，状态：" + st;
      line += rs ? "。" + (rs.length > 160 ? rs.slice(0, 160) + "…" : rs) : "。";
      addSystemMessage(line);
      break;
    }

    default:
      addBotMessage(msg.content || JSON.stringify(msg));
  }
}

// ───── User Scripts（自定义页面脚本）─────
function isUserScriptsAvailable() {
  try {
    chrome.userScripts.getScripts();
    return true;
  } catch {
    return false;
  }
}

function getExtensionsPageLink() {
  return "chrome://extensions?id=" + (chrome.runtime?.id || "");
}

/** 仅通过 userScripts.execute 在指定标签页执行用户代码，返回结果字符串或抛出。 */
async function executeCustomPageScriptViaUserScripts(scriptCode) {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) throw new Error("无法获取当前活动标签页。");
  const sentinel = "\n})();\n//__END_" + Math.random().toString(36).slice(2, 10) + "__";
  const safeCode = scriptCode.indexOf(sentinel) >= 0 ? scriptCode.split(sentinel).join("\n") : scriptCode;
  const wrapped =
    "(function(){ try { var __r = (function() {\n" +
    safeCode +
    sentinel +
    "\nif (__r === undefined || __r === null) return ''; if (typeof __r === 'string') return __r; return JSON.stringify(__r); } catch(e) { return '[Error] ' + (e && e.message ? e.message : String(e)); } })();";
  const results = await chrome.userScripts.execute({ target: { tabId: tab.id }, js: [{ code: wrapped }] });
  const first = results?.[0];
  if (first?.error) return "[Error] " + (first.error.message || String(first.error));
  const v = first?.result;
  if (v === undefined || v === null) return "";
  if (typeof v === "string") return v;
  return JSON.stringify(v);
}

/** run_page_script 中在扩展上下文执行（chrome.tabs），非页面注入 */
const EXTENSION_PAGE_SCRIPT_IDS = new Set([
  "tab_list",
  "tab_list_all_windows",
  "tab_activate",
  "tab_reload",
  "tab_go_back",
  "tab_go_forward",
  "tab_close",
  "tab_open"
]);

async function runExtensionPageScript(scriptId, params) {
  const p = params && typeof params === "object" ? params : {};

  async function getTargetTabId(explicitId) {
    if (explicitId != null && explicitId !== "") {
      const n = Number(explicitId);
      if (!Number.isFinite(n) || n <= 0) throw new Error("失败：tabId 无效。");
      return n;
    }
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) throw new Error("失败：无法解析当前活动标签页。");
    return tab.id;
  }

  if (scriptId === "tab_list" || scriptId === "tab_list_all_windows") {
    const maxTabs = Math.min(200, Math.max(1, Number(p.maxTabs) || 50));
    const allBrowser =
      scriptId === "tab_list_all_windows" || String(p.scope || "").toLowerCase() === "browser";
    const tabs = await chrome.tabs.query(allBrowser ? {} : { currentWindow: true });
    const urlMax = Math.min(500, Math.max(80, Number(p.urlMaxLength) || 200));
    const slice = tabs.slice(0, maxTabs).map((t) => ({
      tabId: t.id,
      windowId: t.windowId,
      title: t.title || "",
      url: truncateTabListUrl(t.url || "", urlMax),
      active: !!t.active,
      index: t.index
    }));
    const scopeLabel = allBrowser ? "当前浏览器（所有窗口）" : "当前窗口";
    return (
      "成功：" +
      scopeLabel +
      "共 " +
      tabs.length +
      " 个标签（下列为前 " +
      slice.length +
      " 个）。\n" +
      JSON.stringify(slice, null, 2)
    );
  }

  if (scriptId === "tab_activate") {
    const tabId = p.tabId != null ? Number(p.tabId) : NaN;
    if (!Number.isFinite(tabId) || tabId <= 0) throw new Error("失败：tab_activate 需要有效的 tabId。");
    await chrome.tabs.update(tabId, { active: true });
    return "成功：已激活标签 tabId=" + tabId + "。";
  }

  if (scriptId === "tab_reload") {
    const tabId = await getTargetTabId(p.tabId);
    await chrome.tabs.reload(tabId);
    return "成功：已刷新标签 tabId=" + tabId + "。";
  }

  if (scriptId === "tab_go_back") {
    const tabId = await getTargetTabId(p.tabId);
    await chrome.tabs.goBack(tabId);
    return "成功：已在标签 tabId=" + tabId + " 执行后退。";
  }

  if (scriptId === "tab_go_forward") {
    const tabId = await getTargetTabId(p.tabId);
    await chrome.tabs.goForward(tabId);
    return "成功：已在标签 tabId=" + tabId + " 执行前进。";
  }

  if (scriptId === "tab_close") {
    const tabId = p.tabId != null ? Number(p.tabId) : NaN;
    if (!Number.isFinite(tabId) || tabId <= 0) {
      throw new Error("失败：tab_close 必须显式传入 tabId，禁止默认关闭当前页以免误关侧栏上下文。");
    }
    const [active] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (active?.id === tabId) {
      throw new Error("失败：不允许关闭当前活动标签页，请先切换到其他标签再关闭。");
    }
    await chrome.tabs.remove(tabId);
    return "成功：已关闭标签 tabId=" + tabId + "。";
  }

  if (scriptId === "tab_open") {
    let url = typeof p.url === "string" ? p.url.trim() : "";
    if (!url) url = "about:blank";
    const tab = await chrome.tabs.create({ url });
    return "成功：已新建标签 tabId=" + (tab.id != null ? tab.id : "?") + " url=" + url + "。";
  }

  throw new Error("未知扩展脚本: " + scriptId);
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
      if (!scriptId || typeof scriptId !== "string") {
        throw new Error("未知脚本 ID: " + (scriptId || ""));
      }
      let paramsObj = params?.scriptParams ?? params?.params;
      if (typeof paramsObj === "string") {
        try {
          paramsObj = paramsObj ? JSON.parse(paramsObj) : {};
        } catch (_) {
          paramsObj = {};
        }
      }
      paramsObj = paramsObj || {};
      if (EXTENSION_PAGE_SCRIPT_IDS.has(scriptId)) {
        result = await runExtensionPageScript(scriptId, paramsObj);
      } else if (typeof PAGE_SCRIPTS[scriptId] === "function") {
        result = await executeInActiveTab(PAGE_SCRIPTS[scriptId], paramsObj);
      } else {
        throw new Error("未知脚本 ID: " + scriptId);
      }
    } else if (method === "run_custom_page_script") {
      const scriptCode = params?.scriptCode;
      if (typeof scriptCode !== "string" || !scriptCode.trim()) {
        throw new Error("run_custom_page_script 需要非空的 scriptCode 参数。");
      }
      if (!isUserScriptsAvailable()) {
        const link = getExtensionsPageLink();
        sendRpcResponse(
          id,
          null,
          "未开启「Allow User Scripts」，无法执行自定义页面脚本。请到扩展详情页打开「Allow User Scripts」开关：" + link
        );
        return;
      }
      result = await executeCustomPageScriptViaUserScripts(scriptCode.trim());
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

// ───── HITL（危险操作人机确认）─────
// 规范源在本文件：Office / WPS 任务窗应对齐此处协议与 DOM 行为；勿以 WPS（含 Vue / public）为参考改 Chrome。
let pendingConfirmId = null;
let hitlWaitIntervalId = null;
const $hitlOverlay = document.getElementById("hitl-overlay");
const $hitlHumanSummary = document.getElementById("hitl-human-summary");
const $hitlRawLabel = document.getElementById("hitl-raw-label");
const $hitlAction = document.getElementById("hitl-action");
const $hitlWaitStatus = document.getElementById("hitl-wait-status");
const $hitlAllowBtn = document.getElementById("hitl-allow-btn");
const $hitlAddToListBtn = document.getElementById("hitl-add-to-list-btn");
const $hitlDenyBtn = document.getElementById("hitl-deny-btn");

function stopHitlWaitTimer() {
  if (hitlWaitIntervalId != null) {
    clearInterval(hitlWaitIntervalId);
    hitlWaitIntervalId = null;
  }
  if ($hitlWaitStatus) $hitlWaitStatus.textContent = "";
}

function startHitlWaitTimer(timeoutSec) {
  stopHitlWaitTimer();
  const limit = typeof timeoutSec === "number" && timeoutSec > 0 ? Math.floor(timeoutSec) : 60;
  const t0 = Date.now();
  const tick = function () {
    if (!$hitlWaitStatus) return;
    const s = Math.floor((Date.now() - t0) / 1000);
    $hitlWaitStatus.textContent =
      "等待确认 · 已 " +
      s +
      "s · 请在约 " +
      limit +
      " 秒内点击「允许」或「拒绝」（超时将视为拒绝并结束本轮）";
  };
  tick();
  hitlWaitIntervalId = setInterval(tick, 1000);
}

function handleConfirmRequest(msg) {
  const requestId = msg.id || msg.requestId;
  const action = msg.content || msg.action || "未知操作";
  const humanSummary = (msg.humanSummary && String(msg.humanSummary).trim()) || "";
  const hitlKind = msg.hitlKind;
  const timeoutRaw = msg.hitlTimeoutSeconds != null ? msg.hitlTimeoutSeconds : msg.HitlTimeoutSeconds;
  const hitlTimeoutSec =
    typeof timeoutRaw === "number"
      ? timeoutRaw
      : typeof timeoutRaw === "string" && /^\d+$/.test(timeoutRaw.trim())
        ? parseInt(timeoutRaw.trim(), 10)
        : 60;
  if (!requestId) {
    debugLog("HITL", "confirm_request missing id", "err");
    return;
  }
  pendingConfirmId = requestId;
  if ($hitlHumanSummary) {
    if (humanSummary) {
      $hitlHumanSummary.textContent = humanSummary;
      $hitlHumanSummary.style.display = "";
    } else {
      $hitlHumanSummary.textContent = "";
      $hitlHumanSummary.style.display = "none";
    }
  }
  if ($hitlRawLabel) $hitlRawLabel.style.display = humanSummary ? "" : "none";
  if ($hitlAction) $hitlAction.textContent = action;
  const showAddToList = hitlKind === "run_command" || hitlKind === "run_page_script";
  if ($hitlAddToListBtn) $hitlAddToListBtn.style.display = showAddToList ? "" : "none";
  if ($hitlOverlay) {
    $hitlOverlay.style.display = "flex";
    $hitlOverlay.setAttribute("aria-hidden", "false");
  }
  startHitlWaitTimer(hitlTimeoutSec);
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
  stopHitlWaitTimer();
  // 关闭时清空 HITL 文案：与 handleConfirmRequest 成对；其它端应对齐本函数（见 .cursor/rules/multi-client-chrome-canonical.mdc）
  if ($hitlHumanSummary) {
    $hitlHumanSummary.textContent = "";
    $hitlHumanSummary.style.display = "none";
  }
  if ($hitlRawLabel) $hitlRawLabel.style.display = "none";
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

// ───── AI candidate option chooser (multi-round) ─────
let askOptionsOverlay = null;
let $askOptionsTitle = null;
let $askOptionsPrompt = null;
let $askOptionsStepIndicator = null;
let $askOptionsQuestion = null;
let $askOptionsOptions = null;
let $askOptionsConfirmBtn = null;

let askOptionsRequestId = null;
let askOptionsSteps = []; // {stepId, question, options:[{optionId,label}]}
let askOptionsSelections = {}; // { [stepId]: optionId }
let askOptionsCurrentStepIndex = 0;
let askOptionsCurrentSelectedOptionId = null;
let askOptionsBound = false;

function openAskOptionsOverlay() {
  if (!askOptionsOverlay) return;
  askOptionsOverlay.style.display = "flex";
  askOptionsOverlay.setAttribute("aria-hidden", "false");
}

function closeAskOptionsOverlay() {
  if (!askOptionsOverlay) return;
  askOptionsOverlay.style.display = "none";
  askOptionsOverlay.setAttribute("aria-hidden", "true");
  askOptionsRequestId = null;
  askOptionsSteps = [];
  askOptionsSelections = {};
  askOptionsCurrentStepIndex = 0;
  askOptionsCurrentSelectedOptionId = null;
}

function setAskOptionsActiveOption(optionId) {
  askOptionsCurrentSelectedOptionId = optionId || null;
  if (!$askOptionsOptions) return;
  const items = $askOptionsOptions.querySelectorAll(".ask-option-item");
  items.forEach(el => {
    const active = el.dataset.optionId === String(optionId || "");
    el.classList.toggle("ask-option-item--active", active);
  });
}

function renderAskOptionsStep(idx) {
  if (!$askOptionsTitle || !$askOptionsPrompt || !$askOptionsStepIndicator || !$askOptionsQuestion || !$askOptionsOptions) return;
  if (!Array.isArray(askOptionsSteps) || askOptionsSteps.length === 0) return;
  const step = askOptionsSteps[idx];
  if (!step) return;

  $askOptionsTitle.textContent = String(step.title || "") || String(askOptionsTitleCache || "请选择一个选项");
  $askOptionsPrompt.textContent = String(askOptionsPromptCache || "");
  $askOptionsStepIndicator.textContent = `步骤 ${idx + 1}/${askOptionsSteps.length}`;
  $askOptionsQuestion.textContent = String(step.question || "");

  const selectedOptionId = askOptionsSelections[step.stepId] || null;
  askOptionsCurrentSelectedOptionId = selectedOptionId;

  const options = Array.isArray(step.options) ? step.options : [];
  $askOptionsOptions.innerHTML = options.map(o => {
    const optionId = String(o.optionId ?? "");
    const label = String(o.label ?? "");
    const active = selectedOptionId && String(selectedOptionId) === optionId ? "ask-option-item--active" : "";
    return `
      <div class="ask-option-item ${active}" data-option-id="${escapeHtml(optionId)}" role="option" aria-selected="${active ? "true" : "false"}">
        <div class="ask-option-label">${escapeHtml(label || optionId)}</div>
      </div>
    `;
  }).join("");
}

let askOptionsTitleCache = "";
let askOptionsPromptCache = "";

function ensureAskOptionsBound() {
  if (askOptionsBound) return;
  askOptionsBound = true;

  if ($askOptionsOptions) {
    $askOptionsOptions.addEventListener("click", (e) => {
      const item = e.target?.closest?.(".ask-option-item");
      if (!item) return;
      setAskOptionsActiveOption(item.dataset.optionId);
    });
  }

  if ($askOptionsConfirmBtn) {
    $askOptionsConfirmBtn.addEventListener("click", () => {
      if (!askOptionsRequestId) return;
      if (!Array.isArray(askOptionsSteps) || askOptionsSteps.length === 0) {
        sendAskOptionsResponse();
        return;
      }

      const step = askOptionsSteps[askOptionsCurrentStepIndex];
      if (!step) return;

      if (!askOptionsCurrentSelectedOptionId) {
        alert("请先选择一个选项后再点击确定。");
        return;
      }

      askOptionsSelections[step.stepId] = String(askOptionsCurrentSelectedOptionId);

      if (askOptionsCurrentStepIndex < askOptionsSteps.length - 1) {
        askOptionsCurrentStepIndex++;
        renderAskOptionsStep(askOptionsCurrentStepIndex);
      } else {
        sendAskOptionsResponse();
      }
    });
  }
}

function sendAskOptionsResponse() {
  const id = askOptionsRequestId;
  const selections = askOptionsSelections || {};
  if (!id) return;

  if (!ws || ws.readyState !== WebSocket.OPEN) {
    addBotMessage("连接已断开，无法提交候选项选择。", true);
    closeAskOptionsOverlay();
    setInputEnabled(true);
    return;
  }

  const payload = JSON.stringify({
    type: "ask_options_response",
    id: id,
    selections: selections
  });
  ws.send(payload);
  debugLog("WS Send", "type=ask_options_response id=" + id + " steps=" + Object.keys(selections).length, "send");

  closeAskOptionsOverlay();
  setInputEnabled(true);
}

function handleAskOptionsRequest(msg) {
  try {
    if (!msg) return;
    const id = msg.id || msg.requestId;
    if (!id) {
      debugLog("ask_options", "missing request id", "err");
      return;
    }

    const steps = Array.isArray(msg.steps) ? msg.steps : [];
    if (!steps.length) {
      askOptionsRequestId = id;
      askOptionsSteps = [];
      askOptionsSelections = {};
      askOptionsCurrentStepIndex = 0;
      askOptionsCurrentSelectedOptionId = null;
      closeAskOptionsOverlay();
      sendAskOptionsResponse();
      return;
    }

    askOptionsOverlay = document.getElementById("ask-options-overlay");
    $askOptionsTitle = document.getElementById("ask-options-title");
    $askOptionsPrompt = document.getElementById("ask-options-prompt");
    $askOptionsStepIndicator = document.getElementById("ask-options-step-indicator");
    $askOptionsQuestion = document.getElementById("ask-options-question");
    $askOptionsOptions = document.getElementById("ask-options-options");
    $askOptionsConfirmBtn = document.getElementById("ask-options-confirm-btn");

    askOptionsRequestId = id;
    askOptionsSteps = steps;
    askOptionsSelections = {};
    askOptionsCurrentStepIndex = 0;
    askOptionsCurrentSelectedOptionId = null;
    askOptionsTitleCache = String(msg.title || "");
    askOptionsPromptCache = String(msg.prompt || "");

    ensureAskOptionsBound();
    openAskOptionsOverlay();
    setInputEnabled(false);
    if ($stopBtn) $stopBtn.style.display = "none";
    renderAskOptionsStep(0);
  } catch (e) {
    console.error("ask_options_request UI error:", e);
    debugLog("ask_options", "ui error " + (e && e.message ? e.message : String(e)), "err");
    addBotMessage("弹出候选项选择失败，请重试。", true);
    closeAskOptionsOverlay();
    setInputEnabled(true);
  }
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

// MCP 工具 run_page_script：预定义脚本注册表，仅执行白名单内 scriptId（函数在页面隔离世界执行，须自包含）
const PAGE_SCRIPTS = {
  scroll_to_top: function () {
    window.scrollTo(0, 0);
    return "成功：已滚动到页面顶部。";
  },
  scroll_to_bottom: function () {
    window.scrollTo(0, document.body.scrollHeight || document.documentElement.scrollHeight);
    return "成功：已滚动到页面底部。";
  },
  get_visible_text: function (params) {
    var p = params || {};
    var text = document.body ? document.body.innerText : "";
    var max = p.maxLength > 0 ? Math.min(500000, Number(p.maxLength)) : 8000;
    if (text.length > max) text = text.slice(0, max) + "\n...(已截断)";
    return text || "(无文本)";
  },
  get_page_title: function () {
    return document.title || "(无标题)";
  },
  get_page_outline: function (params) {
    try {
      var p = params || {};
      var maxLevel = Math.min(6, Math.max(1, Number(p.maxHeadingLevel) || 3));
      var maxHeadings = Math.min(200, Math.max(1, Number(p.maxHeadings) || 50));
      var includeTextPrefix = !!p.includeTextPrefix;
      var maxLen = Math.min(100000, Math.max(0, Number(p.maxLength) || 2000));
      var metaDesc = "";
      var m = document.querySelector('meta[name="description"]');
      if (m && m.content) metaDesc = String(m.content).trim();
      var nodes = document.querySelectorAll("h1, h2, h3, h4, h5, h6");
      var headings = [];
      for (var i = 0; i < nodes.length && headings.length < maxHeadings; i++) {
        var n = nodes[i];
        var lvl = parseInt(n.tagName.slice(1), 10);
        if (lvl > maxLevel) continue;
        var t = (n.innerText || "").replace(/\s+/g, " ").trim();
        if (t) headings.push({ level: lvl, text: t });
      }
      var lines = [];
      lines.push("url: " + (location.href || ""));
      lines.push("title: " + (document.title || ""));
      if (metaDesc) lines.push("meta_description: " + metaDesc);
      lines.push("headings: " + JSON.stringify(headings));
      if (includeTextPrefix && maxLen > 0 && document.body) {
        var prefix = document.body.innerText.replace(/\s+/g, " ").trim().slice(0, maxLen);
        lines.push("text_prefix: " + prefix + (document.body.innerText.length > maxLen ? "\n...(已截断)" : ""));
      }
      return "成功：页面概要如下。\n" + lines.join("\n");
    } catch (e) {
      return "失败：get_page_outline — " + (e && e.message ? e.message : String(e));
    }
  },
  extract_links: function (params) {
    try {
      var p = params || {};
      var maxLinks = Math.min(500, Math.max(1, Number(p.maxLinks) || 100));
      var sameOriginOnly = !!p.sameOriginOnly;
      var origin = location.origin;
      var anchors = document.querySelectorAll("a[href]");
      var out = [];
      for (var i = 0; i < anchors.length && out.length < maxLinks; i++) {
        var a = anchors[i];
        var href = "";
        try {
          href = a.href || "";
        } catch (_) {
          continue;
        }
        if (!href || href.indexOf("javascript:") === 0) continue;
        if (sameOriginOnly) {
          try {
            var u = new URL(href, location.href);
            if (u.origin !== origin) continue;
          } catch (_) {
            continue;
          }
        }
        var label = (a.innerText || a.textContent || "").replace(/\s+/g, " ").trim().slice(0, 500);
        out.push({ text: label || "(无文本)", href: href });
      }
      return "成功：提取 " + out.length + " 条链接。\n" + JSON.stringify(out, null, 2);
    } catch (e) {
      return "失败：extract_links — " + (e && e.message ? e.message : String(e));
    }
  },
  extract_tables: function (params) {
    try {
      var p = params || {};
      var sel = typeof p.selector === "string" && p.selector.trim() ? p.selector.trim() : "table";
      if (sel.length > 500) return "失败：selector 过长。";
      var maxTables = Math.min(20, Math.max(1, Number(p.maxTables) || 5));
      var maxRows = Math.min(200, Math.max(1, Number(p.maxRows) || 30));
      var maxCols = Math.min(50, Math.max(1, Number(p.maxCols) || 20));
      var tables = document.querySelectorAll(sel);
      var parts = [];
      function escCell(s) {
        return String(s == null ? "" : s)
          .replace(/\|/g, "\\|")
          .replace(/\r?\n/g, " ")
          .trim();
      }
      for (var ti = 0; ti < tables.length && ti < maxTables; ti++) {
        var tbl = tables[ti];
        var rows = tbl.querySelectorAll("tr");
        var md = [];
        var rowCount = 0;
        for (var ri = 0; ri < rows.length && rowCount < maxRows; ri++) {
          var cells = rows[ri].querySelectorAll("th, td");
          if (!cells.length) continue;
          var cols = [];
          for (var ci = 0; ci < cells.length && ci < maxCols; ci++) {
            cols.push(escCell(cells[ci].innerText));
          }
          md.push("| " + cols.join(" | ") + " |");
          rowCount++;
          if (rowCount === 1) {
            md.push("| " + cols.map(function () {
              return "---";
            }).join(" | ") + " |");
          }
        }
        if (md.length) {
          parts.push("### table_" + (ti + 1) + "\n" + md.join("\n"));
        }
      }
      if (!parts.length) return "成功：未找到符合条件的表格（selector=" + sel + "）。";
      return "成功：导出 " + parts.length + " 个表格（Markdown）。\n\n" + parts.join("\n\n");
    } catch (e) {
      return "失败：extract_tables — " + (e && e.message ? e.message : String(e));
    }
  },
  scroll_by: function (params) {
    try {
      var p = params || {};
      var dy = Number(p.deltaY);
      if (!isFinite(dy)) return "失败：deltaY 无效。";
      var smooth = !!p.smooth;
      window.scrollBy({ top: dy, behavior: smooth ? "smooth" : "auto" });
      return "成功：已纵向滚动 " + dy + "px。";
    } catch (e) {
      return "失败：scroll_by — " + (e && e.message ? e.message : String(e));
    }
  },
  scroll_into_view: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el) return "失败：未找到元素（selector=" + s + "）。";
      var block = p.block === "start" || p.block === "center" || p.block === "end" || p.block === "nearest" ? p.block : "nearest";
      var inline = p.inline === "start" || p.inline === "center" || p.inline === "end" || p.inline === "nearest" ? p.inline : "nearest";
      el.scrollIntoView({ block: block, inline: inline });
      return "成功：已滚动到元素（selector=" + s + "）。";
    } catch (e) {
      return "失败：scroll_into_view — " + (e && e.message ? e.message : String(e));
    }
  },
  wait_for_selector: async function (params) {
    var p = params || {};
    var s = typeof p.selector === "string" ? p.selector.trim() : "";
    if (!s) return "失败：selector 不能为空。";
    if (s.length > 500) return "失败：selector 过长。";
    var timeoutMs = Math.min(120000, Math.max(100, Number(p.timeoutMs) || 10000));
    var requireVisible = !!p.requireVisible;
    var t0 = Date.now();
    while (Date.now() - t0 < timeoutMs) {
      var el = document.querySelector(s);
      if (el) {
        if (requireVisible) {
          var st = window.getComputedStyle(el);
          var r = el.getBoundingClientRect();
          if (st.display === "none" || st.visibility === "hidden" || r.width < 1 || r.height < 1) {
            await new Promise(function (res) {
              setTimeout(res, 100);
            });
            continue;
          }
        }
        return "成功：已找到元素（selector=" + s + "）。";
      }
      await new Promise(function (res) {
        setTimeout(res, 100);
      });
    }
    return "失败：在 " + timeoutMs + "ms 内未找到元素（selector=" + s + "）。";
  },
  click_selector: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el) return "失败：未找到元素（selector=" + s + "）。";
      if (typeof el.click !== "function") return "失败：该元素不可点击。";
      if (p.doubleClick) {
        el.dispatchEvent(new MouseEvent("dblclick", { bubbles: true, cancelable: true, view: window }));
      } else {
        el.click();
      }
      return "成功：已" + (p.doubleClick ? "双击" : "点击") + "（selector=" + s + "）。";
    } catch (e) {
      return "失败：click_selector — " + (e && e.message ? e.message : String(e));
    }
  },
  fill_input: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el) return "失败：未找到元素（selector=" + s + "）。";
      var tag = el.tagName;
      if (tag !== "INPUT" && tag !== "TEXTAREA") return "失败：元素不是 input/textarea。";
      var val = p.value != null ? String(p.value) : "";
      el.focus();
      el.value = val;
      el.dispatchEvent(new Event("input", { bubbles: true }));
      el.dispatchEvent(new Event("change", { bubbles: true }));
      return "成功：已填充（selector=" + s + "）。";
    } catch (e) {
      return "失败：fill_input — " + (e && e.message ? e.message : String(e));
    }
  },
  select_option: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el || el.tagName !== "SELECT") return "失败：元素不是 select。";
      var v = p.value != null ? String(p.value) : "";
      el.value = v;
      el.dispatchEvent(new Event("change", { bubbles: true }));
      return "成功：已选择 value=" + v + "。";
    } catch (e) {
      return "失败：select_option — " + (e && e.message ? e.message : String(e));
    }
  },
  set_checked: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el) return "失败：未找到元素（selector=" + s + "）。";
      if (el.tagName !== "INPUT" || (el.type !== "checkbox" && el.type !== "radio")) {
        return "失败：元素不是 checkbox/radio。";
      }
      el.checked = !!p.checked;
      el.dispatchEvent(new Event("change", { bubbles: true }));
      return "成功：已设置 checked=" + !!p.checked + "。";
    } catch (e) {
      return "失败：set_checked — " + (e && e.message ? e.message : String(e));
    }
  },
  hover_selector: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el) return "失败：未找到元素（selector=" + s + "）。";
      el.dispatchEvent(new MouseEvent("mouseover", { bubbles: true, cancelable: true, view: window }));
      el.dispatchEvent(new MouseEvent("mouseenter", { bubbles: true, cancelable: true, view: window }));
      return "成功：已派发悬停事件（selector=" + s + "）。";
    } catch (e) {
      return "失败：hover_selector — " + (e && e.message ? e.message : String(e));
    }
  },
  focus_selector: function (params) {
    try {
      var p = params || {};
      var s = typeof p.selector === "string" ? p.selector.trim() : "";
      if (!s) return "失败：selector 不能为空。";
      if (s.length > 500) return "失败：selector 过长。";
      var el = document.querySelector(s);
      if (!el) return "失败：未找到元素（selector=" + s + "）。";
      el.focus();
      return "成功：已聚焦（selector=" + s + "）。";
    } catch (e) {
      return "失败：focus_selector — " + (e && e.message ? e.message : String(e));
    }
  },
  press_key: function (params) {
    try {
      var p = params || {};
      var key = typeof p.key === "string" ? p.key : "";
      if (!key) return "失败：key 不能为空（合成键盘事件，部分站点可能不响应）。";
      var sel = typeof p.selector === "string" ? p.selector.trim() : "";
      var el = null;
      if (sel) {
        if (sel.length > 500) return "失败：selector 过长。";
        el = document.querySelector(sel);
        if (!el) return "失败：未找到元素（selector=" + sel + "）。";
        el.focus();
      } else {
        el = document.activeElement;
        if (!el || el === document.body) el = document.documentElement;
      }
      var code = typeof p.code === "string" && p.code ? p.code : key;
      var evtInit = {
        key: key,
        code: code,
        bubbles: true,
        cancelable: true,
        ctrlKey: !!p.ctrlKey,
        altKey: !!p.altKey,
        shiftKey: !!p.shiftKey,
        metaKey: !!p.metaKey
      };
      el.dispatchEvent(new KeyboardEvent("keydown", evtInit));
      el.dispatchEvent(new KeyboardEvent("keyup", evtInit));
      return "成功：已在目标元素上派发 keydown/keyup（key=" + key + "）。注意：合成事件与真实键盘输入不等价。";
    } catch (e) {
      return "失败：press_key — " + (e && e.message ? e.message : String(e));
    }
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
  const raw = rawText != null ? String(rawText) : "";
  const msgs = document.querySelectorAll(".msg--bot");
  const rounds = document.querySelectorAll(".msg--round");

  let text = raw.trim() ? raw : "";
  if (!text && msgs.length) text = msgs[msgs.length - 1].textContent || "";
  if (!text && rounds.length) text = rounds[rounds.length - 1].innerText || "";
  if (!text) return;

  /** 流式 UI 用 .msg--round + 时间线，无 .msg--bot；占位替换须落在「助手回复」Markdown 容器上 */
  let domPatchEl = null;
  if (msgs.length) domPatchEl = msgs[msgs.length - 1];
  else if (rounds.length) {
    const lastRound = rounds[rounds.length - 1];
    domPatchEl =
      lastRound.querySelector(".timeline-seg--answer .timeline-seg__body--md") || lastRound;
  }

  const startIdx = text.indexOf("<html_canvas>");
  const endIdx = text.indexOf("</html_canvas>");

  let htmlCode = null;
  if (startIdx !== -1 && endIdx !== -1 && endIdx > startIdx) {
    htmlCode = text.substring(startIdx + 13, endIdx).trim();
    if (domPatchEl) {
      const displayHtml = domPatchEl.innerHTML.replace(
        /&lt;html_canvas&gt;[\s\S]*?&lt;\/html_canvas&gt;|<html_canvas>[\s\S]*?<\/html_canvas>/g,
        "<i>[交互式图表已生成在展示板]</i>"
      );
      domPatchEl.innerHTML = displayHtml;
    }
  }

  const hasMermaid = text.includes("```mermaid");
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
  } else if (wsTab.id != null) {
    try {
      await chrome.tabs.update(wsTab.id, { active: true });
    } catch (_) {
      /* ignore */
    }
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

function addBotMessage(text, isError = false, actionButton = null) {
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
  if (isError && actionButton) {
    const list = Array.isArray(actionButton) ? actionButton : [actionButton];
    for (const ab of list) {
      if (ab && ab.label && typeof ab.onClick === "function") {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "msg-action-btn" + (ab.className ? " " + ab.className : "");
        btn.textContent = ab.label;
        btn.addEventListener("click", ab.onClick);
        div.appendChild(btn);
      }
    }
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
  if ($status) {
    $status.className = s.cls;
    let $text = $status.querySelector(".status-text");
    if (!$text) {
      $status.textContent = "";
      const dot = document.createElement("span");
      dot.className = "status-dot";
      $text = document.createElement("span");
      $text.className = "status-text";
      $status.appendChild(dot);
      $status.appendChild($text);
    }
    $text.textContent = s.text;
  }
  if (state === "failed") {
    if ($status) $status.title = "无法连接到后台服务，请确认 OfficeCopilot.Server 已启动";
  } else {
    if ($status) $status.title = "连接状态";
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
  if (currentRoundWrapper) return;

  const attachmentsPayload = buildAttachmentsPayload();

  if (!ws || ws.readyState !== WebSocket.OPEN) {
    addUserMessage(text || (hasAttachments ? "（附图片）" : ""));
    trySetFirstConversationTitlePreview(text || (hasAttachments ? "（附图片）" : ""));
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
  trySetFirstConversationTitlePreview(text || (hasAttachments ? "（附图片）" : ""));

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

// ───── 语音输入（本机 WebSocket → 百炼实时 ASR）─────
(function initVoiceInput() {
  if (!$voiceBtn || !$input) return;

  const STT_OUT_RATE = 16000;
  let isListening = false;
  let voiceShowingListeningHint = false;
  let voiceSttWs = null;
  let voiceAudioStream = null;
  let voiceAudioContext = null;
  let voiceSourceNode = null;
  let voiceScriptNode = null;
  let voiceGainNode = null;
  let voiceSttReady = false;

  function clearVoiceInputHint() {
    voiceShowingListeningHint = false;
    if ($voiceHint) $voiceHint.textContent = "";
    if ($voiceBtn) $voiceBtn.title = "语音输入";
  }

  function showVoiceListeningHint() {
    voiceShowingListeningHint = true;
    const t = "正在听… 再次点击麦克风可停止";
    if ($voiceHint) $voiceHint.textContent = t;
    if ($voiceBtn) $voiceBtn.title = "停止语音输入";
  }

  function setVoiceInputErrorHint(msg) {
    voiceShowingListeningHint = false;
    if ($voiceHint) $voiceHint.textContent = msg;
    else alert(msg);
    if ($voiceBtn) $voiceBtn.title = "语音输入";
  }

  function setListening(flag) {
    isListening = flag;
    if ($voiceBtn) $voiceBtn.classList.toggle("recording", flag);
  }

  function tasklyResampleFloat32Mono(input, inputRate, outputRate) {
    if (inputRate === outputRate || input.length === 0) return input;
    const ratio = inputRate / outputRate;
    const outLen = Math.max(1, Math.floor(input.length / ratio));
    const out = new Float32Array(outLen);
    for (let i = 0; i < outLen; i++) {
      const srcPos = i * ratio;
      const j = Math.floor(srcPos);
      const f = srcPos - j;
      const a = input[j] || 0;
      const b = input[Math.min(j + 1, input.length - 1)] || 0;
      out[i] = a + (b - a) * f;
    }
    return out;
  }

  function tasklyFloatToPcm16leBytes(floatMono) {
    const buf = new ArrayBuffer(floatMono.length * 2);
    const view = new DataView(buf);
    for (let i = 0; i < floatMono.length; i++) {
      let v = floatMono[i];
      if (v > 1) v = 1;
      else if (v < -1) v = -1;
      const s16 = v < 0 ? Math.round(v * 0x8000) : Math.round(v * 0x7fff);
      view.setInt16(i * 2, Math.max(-32768, Math.min(32767, s16)), true);
    }
    return new Uint8Array(buf);
  }

  function stopVoiceGraph() {
    try {
      if (voiceScriptNode) {
        voiceScriptNode.onaudioprocess = null;
        voiceScriptNode.disconnect();
        voiceScriptNode = null;
      }
    } catch (e) { /* ignore */ }
    try {
      if (voiceGainNode) {
        voiceGainNode.disconnect();
        voiceGainNode = null;
      }
    } catch (e) { /* ignore */ }
    try {
      if (voiceSourceNode) {
        voiceSourceNode.disconnect();
        voiceSourceNode = null;
      }
    } catch (e) { /* ignore */ }
    if (voiceAudioContext) {
      voiceAudioContext.close().catch(function () {});
      voiceAudioContext = null;
    }
    if (voiceAudioStream) {
      voiceAudioStream.getTracks().forEach(function (t) {
        t.stop();
      });
      voiceAudioStream = null;
    }
  }

  function closeVoiceSttWs() {
    return new Promise(function (resolve) {
      const w = voiceSttWs;
      voiceSttWs = null;
      voiceSttReady = false;
      if (!w || w.readyState !== WebSocket.OPEN) {
        resolve();
        return;
      }
      try {
        w.send(JSON.stringify({ type: "stop" }));
      } catch (e) { /* ignore */ }
      const done = function () {
        resolve();
      };
      const t = setTimeout(done, 2500);
      w.addEventListener(
        "close",
        function once() {
          w.removeEventListener("close", once);
          clearTimeout(t);
          done();
        },
        { once: true }
      );
      try {
        w.close();
      } catch (e) {
        clearTimeout(t);
        done();
      }
    });
  }

  async function startVoiceSttSession() {
    await tasklyEnsureApiBase();
    const token = await new Promise(function (resolve) {
      chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (r) {
        resolve((r && r[COPILOT_TOKEN_STORAGE_KEY] || "").trim());
      });
    });
    const qs = new URLSearchParams();
    if (token) qs.set("token", token);
    qs.set("mode", "inline");
    const url = TasklyLocalService.tasklySttStreamWsUrl(API_BASE, qs.toString());
    return await new Promise(function (resolve, reject) {
      let settled = false;
      const w = new WebSocket(url);
      voiceSttWs = w;
      w.onmessage = function (ev) {
        let data;
        try {
          data = JSON.parse(ev.data);
        } catch (e) {
          return;
        }
        if (data.type === "ready") {
          voiceSttReady = true;
          if (!settled) {
            settled = true;
            resolve(w);
          }
          return;
        }
        if (data.type === "error" && data.message) {
          if (!settled) {
            settled = true;
            reject(new Error(String(data.message)));
          } else {
            setVoiceInputErrorHint(String(data.message));
          }
          return;
        }
        if (data.type === "final" && data.text) {
          const transcript = String(data.text).trim();
          if (transcript) {
            const cur = $input.value || "";
            $input.value = (cur ? cur + " " : "") + transcript;
          }
        }
      };
      w.onerror = function () {
        if (!settled) {
          settled = true;
          reject(
            new Error(
              "WebSocket 连接失败（请确认本机服务已启动，选项页已保存且与服务端 user-config 中 webSocketAuthToken 一致；并检查「百炼实时语音识别」配置）。"
            )
          );
        }
      };
      w.onclose = function () {
        if (!settled) {
          settled = true;
          reject(new Error("连接已关闭（请检查本机服务与「百炼实时语音识别」配置）。"));
        }
      };
    });
  }

  $voiceBtn.addEventListener("click", async () => {
    if (isListening) {
      stopVoiceGraph();
      await closeVoiceSttWs();
      setListening(false);
      clearVoiceInputHint();
      return;
    }

    try {
      voiceAudioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
      const msg = (err && String(err.message || "")).toLowerCase();
      const name = (err && err.name) || "";
      let userMessage;
      if (name === "NotAllowedError") {
        userMessage = "无法使用麦克风：权限被拒绝。请在扩展权限设置中允许麦克风。";
      } else if (name === "NotFoundError") {
        userMessage = "无法使用麦克风：未检测到麦克风设备。";
      } else {
        userMessage = "无法使用麦克风：" + (err && err.message ? err.message : String(err));
      }
      userMessage += "\n\n可点击下方按钮打开本扩展的站点权限页。";
      addBotMessage(userMessage, true, {
        label: "打开权限设置",
        onClick: () => openTasklyExtensionPermissionSettings()
      });
      return;
    }

    setListening(true);
    showVoiceListeningHint();

    try {
      await startVoiceSttSession();
    } catch (e) {
      stopVoiceGraph();
      if (voiceAudioStream) {
        voiceAudioStream.getTracks().forEach(function (t) {
          t.stop();
        });
        voiceAudioStream = null;
      }
      setListening(false);
      setVoiceInputErrorHint(e && e.message ? e.message : "无法连接语音识别服务。");
      return;
    }

    voiceAudioContext = new (window.AudioContext || window.webkitAudioContext)();
    try {
      await voiceAudioContext.resume();
    } catch (e) { /* ignore */ }
    voiceSourceNode = voiceAudioContext.createMediaStreamSource(voiceAudioStream);
    const chIn = Math.max(1, Math.min(2, voiceSourceNode.channelCount || 1));
    voiceScriptNode = voiceAudioContext.createScriptProcessor(4096, chIn, 1);
    voiceScriptNode.onaudioprocess = function (e) {
      if (!voiceSttReady || !voiceSttWs || voiceSttWs.readyState !== WebSocket.OPEN) {
        e.outputBuffer.getChannelData(0).fill(0);
        return;
      }
      const inBuf = e.inputBuffer;
      const n = inBuf.length;
      const mono = new Float32Array(n);
      if (inBuf.numberOfChannels >= 2) {
        const ch0 = inBuf.getChannelData(0);
        const ch1 = inBuf.getChannelData(1);
        for (let i = 0; i < n; i++) mono[i] = (ch0[i] + ch1[i]) * 0.5;
      } else {
        mono.set(inBuf.getChannelData(0));
      }
      const resampled = tasklyResampleFloat32Mono(mono, voiceAudioContext.sampleRate, STT_OUT_RATE);
      const pcm = tasklyFloatToPcm16leBytes(resampled);
      try {
        voiceSttWs.send(pcm);
      } catch (err) { /* ignore */ }
      e.outputBuffer.getChannelData(0).fill(0);
    };
    voiceGainNode = voiceAudioContext.createGain();
    voiceGainNode.gain.value = 0;
    voiceSourceNode.connect(voiceScriptNode);
    voiceScriptNode.connect(voiceGainNode);
    voiceGainNode.connect(voiceAudioContext.destination);
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
    if (!currentRoundWrapper) return;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: "stop" }));
      debugLog("WS Send", "type=stop", "send");
    }
    finalizeStream();
  });
}

$input.addEventListener("keydown", (e) => {
  if (atModeOpen) {
    if (e.key === "Escape") {
      e.preventDefault();
      closeAtMode();
      return;
    }
    // 主输入框内用 ↑↓ 切换列表（token 未变时 updateAtModeFromTextarea 会跳过，不会重置高亮）。
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setAtActiveIndex(atModeActiveIndex + 1);
      return;
    }
    if (e.key === "ArrowUp") {
      e.preventDefault();
      setAtActiveIndex(atModeActiveIndex - 1);
      return;
    }
    if ((e.key === "Enter" && !e.shiftKey) || e.key === "Tab") {
      e.preventDefault();
      pickActiveAtCandidate();
      return;
    }
  }
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    handleSend();
  }
});

$input.addEventListener("input", () => {
  $input.style.height = "auto";
  $input.style.height = Math.min($input.scrollHeight, 120) + "px";
  scheduleAtModeSync();
});

// ───── Meeting Listener（WebSocket → 百炼实时 ASR，句末由后端落盘）─────

(function initMeetingListener() {
  if (!$meetingBtn || !$meetingPanel || !$meetingStopBtn) return;

  const STT_OUT_RATE = 16000;

  let audioStream = null;
  let audioContext = null;
  let meetingSourceNode = null;
  let meetingScriptNode = null;
  let meetingGainNode = null;
  let meetingStartTime = null;
  let timerInterval = null;
  let isProcessing = false;
  let meetingSessionId = "";
  let meetingFinalCount = 0;
  let meetingListenActive = false;
  /** 为 true 表示正在结束会议（ drain 前），防止侧栏按钮误判为可重新开始。 */
  let meetingStopping = false;
  let meetingSttWs = null;
  let meetingSttReady = false;
  /** @type {HTMLElement | null} */
  let meetingPartialWrap = null;

  function formatTime(ms) {
    const s = Math.floor(ms / 1000);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    if (h > 0) {
      return String(h).padStart(2, "0") + ":" + String(m).padStart(2, "0") + ":" + String(sec).padStart(2, "0");
    }
    return String(m).padStart(2, "0") + ":" + String(sec).padStart(2, "0");
  }

  function updateTimer() {
    if (!meetingStartTime) return;
    $meetingTimer.textContent = formatTime(Date.now() - meetingStartTime);
  }

  function tasklyResampleFloat32MonoMeeting(input, inputRate, outputRate) {
    if (inputRate === outputRate || input.length === 0) return input;
    const ratio = inputRate / outputRate;
    const outLen = Math.max(1, Math.floor(input.length / ratio));
    const out = new Float32Array(outLen);
    for (let i = 0; i < outLen; i++) {
      const srcPos = i * ratio;
      const j = Math.floor(srcPos);
      const f = srcPos - j;
      const a = input[j] || 0;
      const b = input[Math.min(j + 1, input.length - 1)] || 0;
      out[i] = a + (b - a) * f;
    }
    return out;
  }

  function tasklyFloatToPcm16leBytesMeeting(floatMono) {
    const buf = new ArrayBuffer(floatMono.length * 2);
    const view = new DataView(buf);
    for (let i = 0; i < floatMono.length; i++) {
      let v = floatMono[i];
      if (v > 1) v = 1;
      else if (v < -1) v = -1;
      const s16 = v < 0 ? Math.round(v * 0x8000) : Math.round(v * 0x7fff);
      view.setInt16(i * 2, Math.max(-32768, Math.min(32767, s16)), true);
    }
    return new Uint8Array(buf);
  }

  const previewLines = [];

  function appendMeetingLineIncremental(seq, text) {
    previewLines.push({ seq: seq, text: text });
    const wrap = document.createElement("div");
    wrap.className = "meeting-line";
    const timeEl = document.createElement("span");
    timeEl.className = "meeting-line-time";
    timeEl.textContent = "#" + (seq + 1);
    const p = document.createElement("p");
    p.className = "meeting-line-text";
    p.textContent = text;
    wrap.appendChild(timeEl);
    wrap.appendChild(p);
    $meetingPreview.appendChild(wrap);
    $meetingPreview.scrollTop = $meetingPreview.scrollHeight;
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function buildMeetingHtmlDocument() {
    let body = "";
    for (let i = 0; i < previewLines.length; i++) {
      body += "<section class=\"meeting-segment\" data-seq=\"" + previewLines[i].seq + "\"><p>" + escapeHtml(previewLines[i].text) + "</p></section>\n";
    }
    return "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>会议实录 " + escapeHtml(meetingSessionId) + "</title></head><body><h1>会议实录</h1><p>sessionId: " + escapeHtml(meetingSessionId) + "</p>" + body + "</body></html>";
  }

  function downloadMeetingHtml() {
    if (!previewLines.length) {
      addSystemMessage("当前没有可下载的实录段落。");
      return;
    }
    const blob = new Blob([buildMeetingHtmlDocument()], { type: "text/html;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = (meetingSessionId || "meeting") + "_实录.html";
    a.click();
    URL.revokeObjectURL(a.href);
  }

  function clearMeetingPartial() {
    if (meetingPartialWrap && meetingPartialWrap.parentNode) {
      meetingPartialWrap.remove();
    }
    meetingPartialWrap = null;
  }

  function setMeetingPartialText(text) {
    if (!meetingPartialWrap) {
      meetingPartialWrap = document.createElement("div");
      meetingPartialWrap.className = "meeting-line meeting-line--partial";
      const timeEl = document.createElement("span");
      timeEl.className = "meeting-line-time";
      timeEl.textContent = "…";
      const p = document.createElement("p");
      p.className = "meeting-line-text";
      meetingPartialWrap.appendChild(timeEl);
      meetingPartialWrap.appendChild(p);
      $meetingPreview.appendChild(meetingPartialWrap);
    }
    const p = meetingPartialWrap.querySelector(".meeting-line-text");
    if (p) p.textContent = text;
    $meetingPreview.scrollTop = $meetingPreview.scrollHeight;
  }

  function onMeetingSttMessage(ev) {
    let data;
    try {
      data = JSON.parse(ev.data);
    } catch (e) {
      return;
    }
    if (data.type === "error" && data.message) {
      addSystemMessage("会议转写：" + String(data.message));
      return;
    }
    if (data.type === "partial" && data.text != null) {
      setMeetingPartialText(String(data.text));
      return;
    }
    if (data.type === "final" && data.text != null) {
      const t = String(data.text).trim();
      if (!t) return;
      clearMeetingPartial();
      const seq = typeof data.sequence === "number" ? data.sequence : meetingFinalCount;
      meetingFinalCount++;
      appendMeetingLineIncremental(seq, t);
    }
  }

  function openMeetingSttWs() {
    return new Promise(function (resolve, reject) {
      let settled = false;
      tasklyEnsureApiBase().then(function () {
        chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (r) {
          const token = (r && r[COPILOT_TOKEN_STORAGE_KEY] || "").trim();
          const qs = new URLSearchParams();
          if (token) qs.set("token", token);
          qs.set("mode", "meeting");
          qs.set("meetingSessionId", meetingSessionId);
          const url = TasklyLocalService.tasklySttStreamWsUrl(API_BASE, qs.toString());
          meetingSttWs = new WebSocket(url);
          meetingSttWs.onmessage = function bootstrap(ev) {
            let d;
            try {
              d = JSON.parse(ev.data);
            } catch (e) {
              return;
            }
            if (d.type === "ready") {
              meetingSttReady = true;
              meetingSttWs.onmessage = onMeetingSttMessage;
              if (!settled) {
                settled = true;
                resolve();
              }
              return;
            }
            if (d.type === "error") {
              if (!settled) {
                settled = true;
                reject(new Error(String(d.message || "连接失败")));
              }
            }
          };
          meetingSttWs.onerror = function () {
            if (!settled) {
              settled = true;
              reject(
                new Error(
                  "WebSocket 连接失败（请确认本机服务已启动，选项页已保存且与服务端 user-config 中 webSocketAuthToken 一致；并检查「百炼实时语音识别」配置）。"
                )
              );
            }
          };
          meetingSttWs.onclose = function () {
            if (!settled) {
              settled = true;
              reject(new Error("连接已关闭（请检查本机服务与「百炼实时语音识别」配置）。"));
            }
          };
        });
      });
    });
  }

  function closeMeetingSttWs() {
    return new Promise(function (resolve) {
      const w = meetingSttWs;
      meetingSttWs = null;
      meetingSttReady = false;
      if (!w || w.readyState !== WebSocket.OPEN) {
        resolve();
        return;
      }
      try {
        w.send(JSON.stringify({ type: "stop" }));
      } catch (e) { /* ignore */ }
      const t = setTimeout(function () {
        resolve();
      }, 4000);
      w.addEventListener(
        "close",
        function once() {
          clearTimeout(t);
          resolve();
        },
        { once: true }
      );
      try {
        w.close();
      } catch (e) {
        clearTimeout(t);
        resolve();
      }
    });
  }

  function disconnectMeetingAudioGraph() {
    try {
      if (meetingScriptNode) {
        meetingScriptNode.onaudioprocess = null;
        meetingScriptNode.disconnect();
        meetingScriptNode = null;
      }
    } catch (e) { /* ignore */ }
    try {
      if (meetingGainNode) {
        meetingGainNode.disconnect();
        meetingGainNode = null;
      }
    } catch (e) { /* ignore */ }
    try {
      if (meetingSourceNode) {
        meetingSourceNode.disconnect();
        meetingSourceNode = null;
      }
    } catch (e) { /* ignore */ }
  }

  async function startMeeting() {
    if (isProcessing || meetingStopping) return;
    try {
      audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
      const msg = (err && String(err.message || "")).toLowerCase();
      const name = (err && err.name) || "";
      let userMessage;
      if (name === "NotAllowedError") {
        if (msg.includes("dismissed") || msg.includes("closed")) {
          userMessage = "无法使用麦克风：您关闭了权限窗口或未选择允许。请再次点击「会议监听」，在浏览器弹窗中选择「允许」。";
        } else {
          userMessage = "无法使用麦克风：权限被拒绝。请在浏览器地址栏左侧点击锁/图标，将麦克风权限改为「允许」后重试。";
        }
      } else if (name === "NotFoundError") {
        userMessage = "无法使用麦克风：未检测到麦克风设备。";
      } else if (name === "NotReadableError" || name === "AbortError") {
        userMessage = "无法使用麦克风：设备被占用或不可用，请关闭其他使用麦克风的应用后重试。";
      } else {
        userMessage = "无法使用麦克风：权限被拒绝或设备不可用，请检查浏览器设置后重试。";
      }
      userMessage += "\n\n点击下方按钮将在 Chrome 中打开本扩展的站点权限页（可改麦克风）；若被拦截会再尝试打开扩展程序详情页。";
      addBotMessage(userMessage, true, {
        label: "打开权限设置",
        onClick: () => openTasklyExtensionPermissionSettings()
      });
      return;
    }

    meetingSessionId = "meeting_" + (crypto.randomUUID ? crypto.randomUUID().replace(/-/g, "").slice(0, 16) : String(Date.now()));
    previewLines.length = 0;
    meetingFinalCount = 0;
    meetingListenActive = false;
    meetingSttReady = false;
    clearMeetingPartial();
    meetingStartTime = Date.now();
    $meetingPanel.style.display = "block";
    $meetingPreview.innerHTML = "";
    $meetingBtn.classList.add("active");
    timerInterval = setInterval(updateTimer, 1000);

    try {
      await openMeetingSttWs();
    } catch (e) {
      if (audioStream) {
        audioStream.getTracks().forEach(function (t) {
          t.stop();
        });
        audioStream = null;
      }
      if (timerInterval) {
        clearInterval(timerInterval);
        timerInterval = null;
      }
      $meetingPanel.style.display = "none";
      $meetingBtn.classList.remove("active");
      meetingStartTime = null;
      addSystemMessage("无法启动会议转写：" + (e && e.message ? e.message : String(e)));
      return;
    }

    audioContext = new (window.AudioContext || window.webkitAudioContext)();
    try {
      await audioContext.resume();
    } catch (e) { /* ignore */ }
    meetingSourceNode = audioContext.createMediaStreamSource(audioStream);
    const chIn = Math.max(1, Math.min(2, meetingSourceNode.channelCount || 1));
    meetingScriptNode = audioContext.createScriptProcessor(4096, chIn, 1);
    meetingScriptNode.onaudioprocess = function (e) {
      if (!meetingListenActive) {
        e.outputBuffer.getChannelData(0).fill(0);
        return;
      }
      if (!meetingSttReady || !meetingSttWs || meetingSttWs.readyState !== WebSocket.OPEN) {
        e.outputBuffer.getChannelData(0).fill(0);
        return;
      }
      const inBuf = e.inputBuffer;
      const n = inBuf.length;
      const mono = new Float32Array(n);
      if (inBuf.numberOfChannels >= 2) {
        const ch0 = inBuf.getChannelData(0);
        const ch1 = inBuf.getChannelData(1);
        for (let i = 0; i < n; i++) {
          mono[i] = (ch0[i] + ch1[i]) * 0.5;
        }
      } else {
        mono.set(inBuf.getChannelData(0));
      }
      const resampled = tasklyResampleFloat32MonoMeeting(mono, audioContext.sampleRate, STT_OUT_RATE);
      const pcm = tasklyFloatToPcm16leBytesMeeting(resampled);
      try {
        meetingSttWs.send(pcm);
      } catch (err) { /* ignore */ }
      e.outputBuffer.getChannelData(0).fill(0);
    };
    meetingGainNode = audioContext.createGain();
    meetingGainNode.gain.value = 0;
    meetingSourceNode.connect(meetingScriptNode);
    meetingScriptNode.connect(meetingGainNode);
    meetingGainNode.connect(audioContext.destination);
    meetingListenActive = true;

    addSystemMessage(
      "会议监听已开始（实时语音经本机 WebSocket 转写至百炼 ASR）。会话 ID：" +
        meetingSessionId +
        "。下方为语音实录，非 AI 总结；点「结束并总结」或实录页「AI 总结」后再生成纪要。"
    );
    addSystemMessage("已尝试打开「会议实录」标签页（大屏逐条刷新）；请保持本侧栏开启以继续录音。导出 Word/Excel：在对话中说明「根据会议实录 sessionId=" + meetingSessionId + " …」。");
    try {
      chrome.runtime.sendMessage({ type: "OPEN_MEETING_LIVE_TAB", sessionId: meetingSessionId }, function () {
        void chrome.runtime.lastError;
      });
    } catch (e) { /* ignore */ }
  }

  async function stopMeeting() {
    if (isProcessing) return;
    isProcessing = true;
    meetingStopping = true;
    $meetingStopBtn.disabled = true;
    $meetingStopBtn.textContent = "处理中…";

    meetingListenActive = false;

    if (timerInterval) {
      clearInterval(timerInterval);
      timerInterval = null;
    }

    const duration = meetingStartTime ? formatTime(Date.now() - meetingStartTime) : "??:??";
    meetingStartTime = null;

    disconnectMeetingAudioGraph();

    if (audioContext) {
      try {
        await audioContext.close();
      } catch (e) { /* ignore */ }
      audioContext = null;
    }

    if (audioStream) {
      audioStream.getTracks().forEach(function (t) {
        t.stop();
      });
      audioStream = null;
    }

    await closeMeetingSttWs();
    clearMeetingPartial();

    $meetingPanel.style.display = "none";
    $meetingBtn.classList.remove("active");
    $meetingStopBtn.disabled = false;
    $meetingStopBtn.textContent = "结束并总结";
    isProcessing = false;
    meetingStopping = false;

    const sid = meetingSessionId;

    if (meetingFinalCount === 0) {
      addSystemMessage("会议监听已结束（" + duration + "），未识别到可落盘的语音内容。");
      return;
    }

    addSystemMessage("会议监听已结束（" + duration + "），正在请求生成会议纪要（基于已落盘转写）…");

    send(tasklyMeetingSummaryUserContent(sid, "会议监听已结束"));
  }

  $meetingBtn.addEventListener("click", () => {
    if (meetingListenActive || meetingStopping) {
      stopMeeting();
    } else {
      startMeeting();
    }
  });

  $meetingStopBtn.addEventListener("click", () => {
    stopMeeting();
  });

  if ($meetingDownloadBtn) {
    $meetingDownloadBtn.addEventListener("click", function (e) {
      e.stopPropagation();
      downloadMeetingHtml();
    });
  }
  if ($meetingExportHintBtn) {
    $meetingExportHintBtn.addEventListener("click", function (e) {
      e.stopPropagation();
      if (!meetingSessionId) {
        addSystemMessage("请先开始会议监听。");
        return;
      }
      addSystemMessage(
        "导出 Word：在下方输入框发送例如「根据会议实录 sessionId=" + meetingSessionId + " 用 Word 工具生成一份纪要文档并保存到下载文件夹」。\n" +
        "导出 Excel：可请助手将待办事项写成表格并保存到下载文件夹。Chrome 侧无「当前 Word 文档」时，以生成文件 + 下载为主。"
      );
    });
  }
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

// 先拉 Agent 列表再连 WS，保证首连即带 agentProfileId
void (async function tasklySidepanelBoot() {
  try {
    await tasklyEnsureApiBase();
    await refreshAgentProfileSelector();
  } catch (e) {
    console.warn("tasklySidepanelBoot", e);
  }
  connect();
})();
