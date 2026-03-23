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

  initAtModeUI();
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
      const [builtinRes, skillsRes] = await Promise.all([
        fetch(API_BASE + "/api/tools/builtin"),
        fetch(API_BASE + "/api/skills"),
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
        .filter(s => (s.enabled !== false && s.Enabled !== false) && (s.promptTemplate || s.PromptTemplate || ""))
        .map(s => {
          const id = s.id || s.Id || "";
          const safeName = sanitizeSkillFunctionName(id);
          return {
            group: "Skills",
            label: s.name || s.Name || id,
            internal: "UserSkill_" + safeName,
            desc: s.description || s.Description || ""
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
let $atModeFilterInput = null;
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

function openAtMode(filter, startIdx, endIdx) {
  if (!$atModePanel || !$atModeFilterInput || !$atModeListEl) return;
  atModeOpen = true;
  atTokenStart = startIdx;
  atTokenEnd = endIdx;

  $atModeFilterInput.value = filter || "";
  $atModePanel.style.display = "block";
  // Don't steal focus for arrow-key navigation UX; allow user to keep typing in textarea.
  // But if user clicks on the filter input, we can still allow focus.
  atModeActiveIndex = 0;
  renderAtModeList($atModeFilterInput.value || "");
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
}

function getCandidateDisplayParts(c) {
  const internal = c.internal || "";
  return {
    title: c.label || internal || "",
    meta: internal ? `[TOOL:${internal}]` : ""
  };
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

function renderAtModeList(filterRaw) {
  if (!$atModeListEl) return;
  const filter = (filterRaw || "").trim().toLowerCase();
  const list = atModeCandidates || [];
  if (!list.length) {
    const hint = atModeLoadError ? escapeHtml(atModeLoadError) : "暂无可用工具/技能";
    $atModeListEl.innerHTML = `<div class="at-mode-empty">${hint}</div>`;
    atModeActiveIndex = 0;
    return;
  }

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
    // Keep Tools first for UX, then score.
    const g1 = a.c.group === "Tools" ? 0 : 1;
    const g2 = b.c.group === "Tools" ? 0 : 1;
    if (g1 !== g2) return g1 - g2;
    if (b.score !== a.score) return b.score - a.score;
    return String(a.c.label).localeCompare(String(b.c.label), "zh-Hans");
  });

  const top = scored.slice(0, 30).map(x => x.c);
  if (!top.length) {
    $atModeListEl.innerHTML = `<div class="at-mode-empty">无匹配结果</div>`;
    atModeActiveIndex = 0;
    return;
  }

  // When the list changes (filter typed), reset highlight to the first result.
  atModeActiveIndex = 0;

  $atModeListEl.innerHTML = top.map((c, idx) => {
    const parts = getCandidateDisplayParts(c);
    const groupTag = c.group === "Tools" ? "工具" : "技能";
    const safeLabel = escapeHtml(parts.title);
    const safeMeta = escapeHtml(parts.meta);
    const activeCls = idx === atModeActiveIndex ? " at-mode-item--active" : "";
    return `
      <div class="at-mode-item${activeCls}" data-at-idx="${idx}" data-internal="${escapeHtmlAttr(c.internal || "")}" role="option" aria-selected="${idx === atModeActiveIndex ? "true" : "false"}">
        <div class="at-mode-item-title">${safeLabel}</div>
        <div class="at-mode-item-meta">${groupTag} · ${safeMeta}</div>
      </div>
    `;
  }).join("");
}

function insertAtCandidate(candidate) {
  if (!$input || !$atModePanel) return;
  if (!candidate || atTokenStart < 0 || atTokenEnd < 0) return;

  const value = $input.value || "";
  const internal = candidate.internal || "";
  const inserted = `[TOOL:${internal}]`;
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

  // Only open when the '@' is preceded by start or non-word boundary, to avoid
  // triggering in email addresses etc. MVP: allow if previous char is whitespace/punct/start.
  const value = $input.value || "";
  const prev = token.atIndex > 0 ? value[token.atIndex - 1] : "";
  const allow = token.atIndex === 0 || isWhitespace(prev) || /[.,;:!?()[\]{}]/.test(prev);
  if (!allow) {
    if (atModeOpen) closeAtMode();
    return;
  }

  // Ensure candidates are ready before opening list.
  if (!atModeLoaded) await loadAtModeCandidates();

  const filter = token.filter || "";
  // 若 @ 区间与过滤串未变（例如仅按了方向键切换列表高亮），不要再次 openAtMode/render，
  // 否则会重置 atModeActiveIndex，表现为「上下箭头无法切换选择」。
  if (
    atModeOpen &&
    atTokenStart === token.atIndex &&
    atTokenEnd === token.caret &&
    ($atModeFilterInput?.value || "") === filter
  ) {
    return;
  }
  openAtMode(filter, token.atIndex, token.caret);
}

function initAtModeUI() {
  $atModePanel = document.getElementById("at-mode-panel");
  $atModeFilterInput = document.getElementById("at-mode-filter-input");
  $atModeListEl = document.getElementById("at-mode-list");
  if (!$atModePanel || !$atModeFilterInput || !$atModeListEl) return;

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

  // Filter input changes (keep textarea token in sync)
  $atModeFilterInput.addEventListener("input", () => {
    if (!atModeOpen || atTokenStart < 0 || atTokenEnd < 0) return;
    const newFilter = $atModeFilterInput.value || "";
    const value = $input.value || "";
    const tokenStart = atTokenStart + 1;
    // Replace the old token text area with newFilter
    const newValue = value.slice(0, tokenStart) + newFilter + value.slice(atTokenEnd);
    $input.value = newValue;
    atTokenEnd = tokenStart + newFilter.length;
    renderAtModeList(newFilter);
    // Keep textarea caret at end of token for consistent insertion.
    $input.setSelectionRange(atTokenEnd, atTokenEnd);
  });

  // Keyboard navigation inside filter input
  $atModeFilterInput.addEventListener("keydown", (e) => {
    if (!atModeOpen) return;
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setAtActiveIndex(atModeActiveIndex + 1);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setAtActiveIndex(atModeActiveIndex - 1);
    } else if (e.key === "Enter" || e.key === "Tab") {
      e.preventDefault();
      pickActiveAtCandidate();
    } else if (e.key === "Escape") {
      e.preventDefault();
      closeAtMode();
      $input.focus();
    }
  });

  // Keep list in sync with textarea edits
  $input.addEventListener("keyup", () => {
    // Ignore when user is interacting with filter input (we already update list there).
    if (document.activeElement === $atModeFilterInput) return;
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

// ───── Debug Panel & Runtime Log ─────
const DEBUG_LOG_MAX = 300;
const debugLogBuffer = [];
const $debugPanel = document.getElementById("debug-panel");
const $toggleDebugBtn = document.getElementById("toggle-debug-btn");
const $closeDebugBtn = document.getElementById("close-debug-btn");
const $debugContent = document.getElementById("debug-content");
const $debugRuntimeLog = document.getElementById("debug-runtime-log");
const $clearRuntimeLogBtn = document.getElementById("clear-runtime-log-btn");
const $openDebugStatsBtn = document.getElementById("open-debug-stats-btn");

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
if ($openDebugStatsBtn && typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.getURL && chrome.tabs) {
  $openDebugStatsBtn.addEventListener("click", () => {
    const url = chrome.runtime.getURL("debug-stats.html");
    chrome.tabs.create({ url });
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
    flushCrossAgentAutoRunAfterReconnect();
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
// 本轮回复：时间线（prep / think / intent / digest / tool / subtask）+ 底部完整结论区
let currentRoundWrapper = null;
let timelineRoot = null;
let openPrepSeg = null;
let openThinkSeg = null;
let openDigestSeg = null;
let openIntentSeg = null;
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

function newTimelineSeg(kind, titleLabel) {
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
  timelineRoot.appendChild(d);
  return { details: d, pre, tail };
}

function collapseSeg(ref) {
  if (ref && ref.details) ref.details.open = false;
}

function collapseAllOpenPhases() {
  collapseSeg(openPrepSeg);
  openPrepSeg = null;
  collapseSeg(openThinkSeg);
  openThinkSeg = null;
  collapseSeg(openDigestSeg);
  openDigestSeg = null;
  collapseSeg(openIntentSeg);
  openIntentSeg = null;
  closeOpenAnswerSegment();
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

function appendReasoningChunk(text) {
  const t = text != null ? String(text) : "";
  if (!t) return;
  if (!currentRoundWrapper) beginStream();
  if (!openThinkSeg) openThinkSeg = newTimelineSeg("think", "推理");
  openThinkSeg.pre.textContent += t;
  openThinkSeg.tail.textContent = timelineTail(openThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
  openThinkSeg.details.title = openThinkSeg.pre.textContent;
  if ($messages) $messages.scrollTop = $messages.scrollHeight;
}

function beginStream() {
  removeThinkingIndicator();
  const welcome = $messages.querySelector(".welcome");
  if (welcome) welcome.remove();

  currentRoundWrapper = document.createElement("div");
  currentRoundWrapper.className = "msg msg--round";
  timelineRoot = null;
  openPrepSeg = null;
  openThinkSeg = null;
  openDigestSeg = null;
  openIntentSeg = null;
  openAnswerSeg = null;

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

function appendStreamChunk(text) {
  if (!currentRoundWrapper) beginStream();
  collapseSeg(openThinkSeg);
  openThinkSeg = null;
  collapseSeg(openDigestSeg);
  openDigestSeg = null;
  const chunk = text != null ? String(text) : "";
  if (!chunk) return;
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
  const wrap = currentRoundWrapper;
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

    case "agent_status": {
      const line = (msg.content && String(msg.content).trim()) || "";
      if (line) appendAgentStatusLine(line);
      break;
    }

    case "agent_trace":
      appendAgentTrace(msg);
      break;

    case "reasoning_chunk":
      appendReasoningChunk(msg.content);
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
      if (!currentRoundWrapper) beginStream();
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
      collapseAllOpenPhases();
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

    case "ask_options_request":
      handleAskOptionsRequest(msg);
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
  if (isError && actionButton && actionButton.label && typeof actionButton.onClick === 'function') {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'msg-action-btn';
    btn.textContent = actionButton.label;
    btn.addEventListener('click', actionButton.onClick);
    div.appendChild(btn);
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
  if (currentRoundWrapper) return;

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
      const openOptions = () => { if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.openOptionsPage) chrome.runtime.openOptionsPage(); };
      addBotMessage(userMessage, true, { label: "打开扩展设置", onClick: openOptions });
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
