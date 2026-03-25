(function () {
  "use strict";

  var WS_URL = "ws://127.0.0.1:8765/ws";
  var API_BASE = "http://127.0.0.1:8765";
  var tasklyOfficeApiReady = null;
  function tasklyEnsureOfficeApiBase() {
    if (tasklyOfficeApiReady) return tasklyOfficeApiReady;
    tasklyOfficeApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(null).then(function (r) {
      var hw = TasklyLocalService.tasklyHttpWsFromBase(r.baseUrl);
      API_BASE = hw.apiBase;
      WS_URL = hw.wsUrl;
    });
    return tasklyOfficeApiReady;
  }
  var TASKLY_AUTH_TOKEN_KEY = "tasklyLocalServiceAuthToken";
  function getStoredAuthToken() {
    try { return (localStorage.getItem(TASKLY_AUTH_TOKEN_KEY) || "").trim(); } catch (e) { return ""; }
  }
  function tasklyFetch(url, init) {
    init = init || {};
    var headers = new Headers(init.headers || {});
    var t = getStoredAuthToken();
    if (t) headers.set("X-OfficeCopilot-Token", t);
    return fetch(url, Object.assign({}, init, { headers: headers }));
  }
  function ensureBootstrapAuthToken() {
    return tasklyEnsureOfficeApiBase().then(function () {
    return fetch(API_BASE + "/api/bootstrap/local-service-auth")
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j || !j.ok) return;
        var t = (j.webSocketAuthToken || "").trim();
        if (!t || getStoredAuthToken()) return;
        try { localStorage.setItem(TASKLY_AUTH_TOKEN_KEY, t); } catch (e) {}
      })
      .catch(function () {});
    });
  }
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 16000;

  let OFFICE_CLIENT_TYPE = "office"; // set after Office.onReady to office-word | office-excel | office-powerpoint

  (function tasklySyncThemeFromBackend() {
    try {
      tasklyEnsureOfficeApiBase().then(function () {
      tasklyFetch(API_BASE + "/api/config")
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (j) {
          if (!j || typeof TasklyTheme === "undefined") return;
          var id = j.uiThemeId || j.UiThemeId;
          if (id) TasklyTheme.setTheme(id);
          if (typeof window.tasklyOfficeRefreshEmbedThemes === "function") window.tasklyOfficeRefreshEmbedThemes();
        })
        .catch(function () {});
      }).catch(function () {});
    } catch (e) {}
  })();

  const $messages = document.getElementById("messages");
  const $input = document.getElementById("input");
  const $sendBtn = document.getElementById("send-btn");
  const $stopBtn = document.getElementById("stop-btn");
  const $status = document.getElementById("status");
  const $planPanel = document.getElementById("plan-panel");
  const $planPanelTitle = document.getElementById("plan-panel-title");
  const $planContentView = document.getElementById("plan-content-view");
  const $planContentEdit = document.getElementById("plan-content-edit");
  const $planExecuteBtn = document.getElementById("plan-execute-btn");
  const $planEditBtn = document.getElementById("plan-edit-btn");
  const $planSaveBtn = document.getElementById("plan-save-btn");
  const $planCancelEditBtn = document.getElementById("plan-cancel-edit-btn");
  const $planChecklistWrap = document.getElementById("plan-checklist-wrap");
  const $planChecklistList = document.getElementById("plan-checklist-list");
  const $planChecklistSummary = document.getElementById("plan-checklist-summary");
  const $newChatBtn = document.getElementById("new-chat-btn");
  const $attachBtn = document.getElementById("attach-btn");
  const $fileInput = document.getElementById("file-input");
  const $attachmentsPreview = document.getElementById("attachments-preview");
  const $atModePanel = document.getElementById("at-mode-panel");
  const $atModeList = document.getElementById("at-mode-list");

  const STORAGE_PLAN_STEP_INDEX = "copilot_plan_step_index";

  let currentPlanId = null;
  let currentPlanTitle = null;
  let currentPlanContent = null;
  let currentPlanCreatedBy = null;

  /** @type {{ index: number, title: string }[]} */
  let planChecklistSteps = [];
  /** @type {Record<number, string>} */
  let planChecklistStatus = {};

  /** @type {{ id: string, mimeType: string, data: string }[]} */
  let attachments = [];

  let atModeOpen = false;
  let atModeActiveIndex = 0;
  let atTokenStart = -1;
  let atTokenEnd = -1;
  let atModeFilterStr = "";
  /** @type {{ group: string, label: string, internal: string, desc: string }[]} */
  let atModeCandidates = [];
  /** @type {{ group: string, label: string, internal: string, desc: string }[]} */
  let atModeTopList = [];
  let atModeLoaded = false;
  let atModeLoadError = "";
  let atModeBootstrapping = false;
  let atModeLoadingPromise = null;
  let atModeSyncScheduled = false;

  let ws = null;
  let sessionId = null;
  let reconnectDelay = RECONNECT_BASE_MS;
  let reconnectTimer = null;
  let pendingMessages = [];
  let crossAgentAutoRunLock = false;
  let crossAgentAutoRunQueued = false;
  const CROSS_AGENT_AUTO_TRIGGER_TEXT =
    "请根据系统说明中「来自其他端的待办」逐项执行；每完成一项请调用 complete_cross_agent_task 标记完成。除待办外请勿延伸闲聊。";

  function getSessionId() {
    let id = sessionStorage.getItem("copilot_session_id");
    if (!id) {
      id = crypto.randomUUID().replace(/-/g, "").slice(0, 12);
      sessionStorage.setItem("copilot_session_id", id);
    }
    return id;
  }

  function setStatus(connected) {
    if (!$status) return;
    $status.className = connected ? "status status--connected" : "status status--disconnected";
    const $text = $status.querySelector(".status-text");
    if ($text) $text.textContent = connected ? "已连接" : "未连接";
  }

  function addSystemMessage(text) {
    const welcome = $messages.querySelector(".welcome");
    if (welcome) welcome.remove();
    const div = document.createElement("div");
    div.className = "msg msg--system";
    div.textContent = text;
    $messages.appendChild(div);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function addUserMessage(text) {
    const welcome = $messages.querySelector(".welcome");
    if (welcome) welcome.remove();
    const div = document.createElement("div");
    div.className = "msg msg--user";
    div.textContent = text || "";
    $messages.appendChild(div);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function addBotMessage(text, isError) {
    const welcome = $messages.querySelector(".welcome");
    if (welcome) welcome.remove();
    const div = document.createElement("div");
    div.className = "msg msg--bot" + (isError ? " msg--error" : "");
    if (typeof marked !== "undefined") {
      div.innerHTML = marked.parse(isError && text ? "⚠️ " + text : text || "");
    } else {
      div.textContent = (isError && text ? "⚠️ " : "") + (text || "");
    }
    $messages.appendChild(div);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function escapeHtml(unsafe) {
    if (!unsafe) return "";
    return String(unsafe)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function getPlanCurrentStepIndex() {
    const v = sessionStorage.getItem(STORAGE_PLAN_STEP_INDEX);
    const n = parseInt(v, 10);
    return n >= 1 ? n : 1;
  }

  function setPlanCurrentStepIndex(stepIndex) {
    if (stepIndex >= 1) sessionStorage.setItem(STORAGE_PLAN_STEP_INDEX, String(stepIndex));
  }

  function clearPlanCurrentStepIndex() {
    try {
      sessionStorage.removeItem(STORAGE_PLAN_STEP_INDEX);
    } catch (e) { /* ignore */ }
  }

  function parsePlanStepsFromContent(content) {
    if (!content || typeof content !== "string") return [];
    const steps = [];
    const re = /^#{1,6}\s*步骤\s*(\d+)\s*$/gm;
    const indices = [];
    let m;
    while ((m = re.exec(content)) !== null) indices.push({ num: parseInt(m[1], 10), pos: m.index });
    for (let i = 0; i < indices.length; i++) {
      const start = indices[i].pos;
      const end = i + 1 < indices.length ? indices[i + 1].pos : content.length;
      const block = content.slice(start, end).trim();
      const firstLine = (block.split(/\r?\n/)[0] || "").trim();
      const title =
        firstLine.replace(/^#{1,6}\s*步骤\s*\d+\s*/, "").trim() ||
        "步骤 " + indices[i].num;
      steps.push({ index: indices[i].num, title: title.slice(0, 60) });
    }
    return steps;
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
    const done = Object.values(planChecklistStatus).filter(function (s) { return s === "done"; }).length;
    if ($planChecklistSummary) $planChecklistSummary.textContent = "执行进度 (" + done + "/" + total + ")";
    planChecklistSteps.forEach(function (s) {
      const status = planChecklistStatus[s.index] || "pending";
      const li = document.createElement("li");
      li.className = "plan-step plan-step--" + status;
      li.dataset.stepIndex = String(s.index);
      const icon = document.createElement("span");
      icon.className = "plan-step-icon";
      icon.textContent = status === "done" ? "✓" : status === "in_progress" ? "◐" : "○";
      const tit = document.createElement("span");
      tit.className = "plan-step-title";
      tit.textContent = "步骤 " + s.index + ": " + s.title;
      li.appendChild(icon);
      li.appendChild(tit);
      $planChecklistList.appendChild(li);
    });
  }

  function updateChecklistStep(stepIndex, status) {
    if (!stepIndex || stepIndex < 1) return;
    planChecklistStatus[stepIndex] = status;
    renderPlanChecklist();
  }

  function initPlanChecklistFromContent(content) {
    planChecklistSteps = parsePlanStepsFromContent(content);
    planChecklistStatus = {};
    planChecklistSteps.forEach(function (s) {
      planChecklistStatus[s.index] = "pending";
    });
    setPlanCurrentStepIndex(1);
    renderPlanChecklist();
  }

  function renderAttachmentsPreview() {
    if (!$attachmentsPreview) return;
    $attachmentsPreview.innerHTML = "";
    if (attachments.length === 0) {
      $attachmentsPreview.style.display = "none";
      return;
    }
    $attachmentsPreview.style.display = "flex";
    attachments.forEach(function (att) {
      const wrap = document.createElement("div");
      wrap.className = "attachment-thumb-wrap";
      const img = document.createElement("img");
      img.className = "attachment-thumb";
      img.alt = "";
      img.src = "data:" + att.mimeType + ";base64," + att.data;
      const removeBtn = document.createElement("button");
      removeBtn.type = "button";
      removeBtn.className = "attachment-remove";
      removeBtn.textContent = "×";
      removeBtn.title = "移除";
      const idToRemove = att.id;
      removeBtn.addEventListener("click", function () {
        attachments = attachments.filter(function (a) {
          return a.id !== idToRemove;
        });
        renderAttachmentsPreview();
      });
      wrap.appendChild(img);
      wrap.appendChild(removeBtn);
      $attachmentsPreview.appendChild(wrap);
    });
  }

  function buildAttachmentsPayload() {
    return attachments.map(function (a) {
      return { mimeType: a.mimeType, data: a.data };
    });
  }

  function addFilesAsAttachments(files) {
    const imageFiles = Array.prototype.filter.call(files || [], function (f) {
      return f && f.type && f.type.indexOf("image/") === 0;
    });
    if (imageFiles.length === 0) return Promise.resolve();
    const tasks = Array.prototype.map.call(imageFiles, function (file) {
      return new Promise(function (resolve) {
        const reader = new FileReader();
        reader.onload = function () {
          const dataUrl = String(reader.result || "");
          const match = dataUrl.match(/^data:([^;]+);base64,(.+)$/);
          const mime = match ? match[1] : "image/png";
          const data = match ? match[2] : "";
          if (data) {
            attachments.push({
              id: Date.now() + "-" + Math.random(),
              mimeType: mime || "image/png",
              data: data
            });
          }
          resolve();
        };
        reader.onerror = function () { resolve(); };
        reader.readAsDataURL(file);
      });
    });
    return Promise.all(tasks).then(function () { renderAttachmentsPreview(); });
  }

  function sanitizeSkillFunctionName(skillId) {
    if (!skillId) return "Skill";
    let s = String(skillId).trim().replace(/-/g, "_").replace(/\//g, "_").replace(/ /g, "_");
    let out = "";
    let prevUnderscore = false;
    for (let i = 0; i < s.length; i++) {
      const c = s[i];
      if (/[A-Za-z0-9_]/.test(c)) {
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

  function isWhitespaceCh(ch) {
    return /\s/.test(ch);
  }

  function findAtTokenInTextarea() {
    if (!$input) return null;
    const value = $input.value || "";
    const caret = $input.selectionStart != null ? $input.selectionStart : 0;
    const left = caret - 1;
    if (left < 0) return null;
    let i = left;
    let lastAt = -1;
    while (i >= 0) {
      const ch = value[i];
      if (isWhitespaceCh(ch)) break;
      if (ch === "@") lastAt = i;
      i--;
    }
    if (lastAt < 0) return null;
    return { atIndex: lastAt, caret: caret, filter: value.slice(lastAt + 1, caret) };
  }

  function loadAtModeCandidates() {
    if (atModeLoaded) return Promise.resolve();
    if (atModeLoadingPromise) return atModeLoadingPromise;
    atModeLoadingPromise = tasklyEnsureOfficeApiBase().then(function () {
    return tasklyFetch(API_BASE + "/api/tools/builtin")
      .then(function (builtinRes) {
        return tasklyFetch(API_BASE + "/api/skills").then(function (skillsRes) {
          return { builtinRes: builtinRes, skillsRes: skillsRes };
        });
      })
      .then(function (_ref) {
        const builtinRes = _ref.builtinRes;
        const skillsRes = _ref.skillsRes;
        const loadErrs = [];
        if (!builtinRes.ok) loadErrs.push("内置工具接口 HTTP " + builtinRes.status);
        if (!skillsRes.ok) loadErrs.push("技能接口 HTTP " + skillsRes.status);
        return Promise.all([
          builtinRes.ok ? builtinRes.json() : Promise.resolve([]),
          skillsRes.ok ? skillsRes.json() : Promise.resolve([])
        ]).then(function (arr) {
          return { builtins: arr[0], skills: arr[1], loadErrs: loadErrs };
        });
      })
      .then(function (pack) {
        const builtins = Array.isArray(pack.builtins) ? pack.builtins : [];
        const skills = Array.isArray(pack.skills) ? pack.skills : [];
        const builtinCandidates = builtins
          .map(function (t) {
            return {
              group: "Tools",
              label: t.name || t.id || "",
              internal: t.id || t.Id || "",
              desc: t.description || t.Description || ""
            };
          })
          .filter(function (c) {
            return c.internal;
          });
        const skillCandidates = skills
          .filter(function (s) {
            return (s.enabled !== false && s.Enabled !== false) && (s.promptTemplate || s.PromptTemplate || "");
          })
          .map(function (s) {
            const id = s.id || s.Id || "";
            const safeName = sanitizeSkillFunctionName(id);
            return {
              group: "Skills",
              label: s.name || s.Name || id,
              internal: "UserSkill_" + safeName,
              desc: s.description || s.Description || ""
            };
          });
        builtinCandidates.sort(function (a, b) {
          return String(a.label).localeCompare(String(b.label), "zh-Hans");
        });
        skillCandidates.sort(function (a, b) {
          return String(a.label).localeCompare(String(b.label), "zh-Hans");
        });
        atModeCandidates = builtinCandidates.concat(skillCandidates);
        atModeLoaded = true;
        atModeLoadError = "";
        if (pack.loadErrs.length) {
          atModeLoadError =
            "部分数据加载失败：" +
            pack.loadErrs.join("；") +
            "。请确认本机后台已启动（" +
            API_BASE +
            "）。" +
            (atModeCandidates.length ? " 以下为已成功加载的条目。" : "");
        }
        if (!atModeCandidates.length) {
          atModeLoadError =
            (atModeLoadError ? atModeLoadError + " " : "") + "当前没有可用的工具或技能可选。";
        }
      })
      .catch(function (e) {
        console.warn("Failed to load @ mode candidates", e);
        atModeCandidates = [];
        atModeLoaded = true;
        atModeLoadError =
          "无法加载工具/技能列表：" +
          (e && e.message ? e.message : String(e)) +
          "。请确认本机后台已启动。";
      })
      .then(function () {
        atModeLoadingPromise = null;
      });
    });
    return atModeLoadingPromise;
  }

  function rebuildAtModeList(filterRaw) {
    const filter = (filterRaw || "").trim().toLowerCase();
    if (!atModeCandidates.length) {
      atModeTopList = [];
      atModeActiveIndex = 0;
      return;
    }
    const scored = [];
    for (let i = 0; i < atModeCandidates.length; i++) {
      const c = atModeCandidates[i];
      const label = String(c.label || "").toLowerCase();
      const internal = String(c.internal || "").toLowerCase();
      const text = label + " " + internal;
      if (filter && text.indexOf(filter) === -1) continue;
      let score = 0;
      if (!filter) score = 1;
      else if (label.indexOf(filter) === 0 || internal.indexOf(filter) === 0) score = 100;
      else if (label.indexOf(filter) !== -1 || internal.indexOf(filter) !== -1) score = 50;
      scored.push({ c: c, score: score });
    }
    scored.sort(function (a, b) {
      const g1 = a.c.group === "Skills" ? 0 : 1;
      const g2 = b.c.group === "Skills" ? 0 : 1;
      if (g1 !== g2) return g1 - g2;
      if (b.score !== a.score) return b.score - a.score;
      return String(a.c.label).localeCompare(String(b.c.label), "zh-Hans");
    });
    atModeTopList = scored.map(function (x) {
      return x.c;
    });
    atModeActiveIndex = 0;
  }

  function renderAtModeListUI() {
    if (!$atModeList) return;
    $atModeList.innerHTML = "";
    let placeholder = "";
    if (atModeBootstrapping) placeholder = "正在加载工具/技能列表…";
    else if (!atModeCandidates.length) placeholder = atModeLoadError || "暂无可用工具/技能";
    else if (!atModeTopList.length) placeholder = "";
    if (placeholder) {
      const empty = document.createElement("div");
      empty.className = "at-mode-empty";
      empty.textContent = placeholder;
      $atModeList.appendChild(empty);
      return;
    }
    for (let idx = 0; idx < atModeTopList.length; idx++) {
      const c = atModeTopList[idx];
      if (idx > 0 && c.group === "Tools" && atModeTopList[idx - 1].group === "Skills") {
        const sep = document.createElement("div");
        sep.className = "at-mode-separator";
        sep.setAttribute("role", "separator");
        sep.setAttribute("aria-hidden", "true");
        $atModeList.appendChild(sep);
      }
      const div = document.createElement("div");
      div.className = "at-mode-item" + (idx === atModeActiveIndex ? " at-mode-item--active" : "");
      div.setAttribute("role", "option");
      div.dataset.atIdx = String(idx);
      const t = document.createElement("div");
      t.className = "at-mode-item-title";
      t.textContent = c.label || c.internal || "";
      div.appendChild(t);
      $atModeList.appendChild(div);
    }
    var activeEl = $atModeList.querySelector(".at-mode-item--active");
    if (activeEl && typeof activeEl.scrollIntoView === "function") {
      activeEl.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
  }

  function openAtMode(filter, startIdx, endIdx) {
    atTokenStart = startIdx;
    atTokenEnd = endIdx;
    atModeFilterStr = filter || "";
    atModeActiveIndex = 0;
    rebuildAtModeList(atModeFilterStr);
    if (!atModeBootstrapping && atModeTopList.length === 0) {
      closeAtMode();
      return;
    }
    atModeOpen = true;
    if ($atModePanel) $atModePanel.style.display = "flex";
    renderAtModeListUI();
  }

  function closeAtMode() {
    atModeOpen = false;
    atModeActiveIndex = 0;
    atTokenStart = -1;
    atTokenEnd = -1;
    atModeFilterStr = "";
    atModeTopList = [];
    if ($atModePanel) $atModePanel.style.display = "none";
  }

  function setAtActiveIndex(idx) {
    const n = atModeTopList.length;
    if (!n) return;
    atModeActiveIndex = Math.max(0, Math.min(n - 1, idx));
    renderAtModeListUI();
  }

  function insertAtCandidate(candidate) {
    if (!candidate || atTokenStart < 0 || atTokenEnd < 0 || !$input) return;
    const value = $input.value || "";
    const internal = candidate.internal || "";
    const inserted = "[TOOL:" + internal + "]";
    const afterChar = value[atTokenEnd] || "";
    const trailing = afterChar && !/\s/.test(afterChar) ? " " : "";
    const newValue = value.slice(0, atTokenStart) + inserted + trailing + value.slice(atTokenEnd);
    $input.value = newValue;
    const newCaret = atTokenStart + inserted.length + trailing.length;
    closeAtMode();
    setTimeout(function () {
      $input.focus();
      $input.setSelectionRange(newCaret, newCaret);
    }, 0);
  }

  function pickAtModeActive() {
    const c = atModeTopList[atModeActiveIndex];
    if (c) insertAtCandidate(c);
  }

  function updateAtModeFromTextarea() {
    if (!$input) return;
    const token = findAtTokenInTextarea();
    if (!token) {
      if (atModeOpen) closeAtMode();
      return;
    }
    const value = $input.value || "";
    const prev = token.atIndex > 0 ? value[token.atIndex - 1] : "";
    var allow = token.atIndex === 0 || !/[A-Za-z0-9_]/.test(prev);
    if (!allow) {
      if (atModeOpen) closeAtMode();
      return;
    }
    if (!atModeLoaded) {
      atModeBootstrapping = true;
      renderAtModeListUI();
      loadAtModeCandidates().then(function () {
        atModeBootstrapping = false;
        if (atModeOpen) {
          rebuildAtModeList(atModeFilterStr);
          if (!atModeTopList.length) {
            closeAtMode();
          } else {
            renderAtModeListUI();
          }
        }
      });
    }
    const filter = token.filter || "";
    if (atModeOpen && atTokenStart === token.atIndex && atTokenEnd === token.caret) {
      return;
    }
    if (atModeLoaded && !atModeBootstrapping) {
      rebuildAtModeList(filter);
      if (!atModeTopList.length) {
        if (atModeOpen) closeAtMode();
        return;
      }
    }
    openAtMode(filter, token.atIndex, token.caret);
  }

  function scheduleAtModeSync() {
    if (atModeSyncScheduled) return;
    atModeSyncScheduled = true;
    queueMicrotask(function () {
      atModeSyncScheduled = false;
      updateAtModeFromTextarea();
    });
  }

  function resetOfficeConversation() {
    closeAtMode();
    try {
      sessionStorage.removeItem("copilot_session_id");
    } catch (e) { /* ignore */ }
    pendingMessages = [];
    crossAgentAutoRunLock = false;
    crossAgentAutoRunQueued = false;
    attachments = [];
    renderAttachmentsPreview();
    currentPlanId = null;
    currentPlanTitle = null;
    currentPlanContent = null;
    currentPlanCreatedBy = null;
    if ($planPanel) $planPanel.style.display = "none";
    planChecklistSteps = [];
    planChecklistStatus = {};
    if ($planChecklistWrap) $planChecklistWrap.style.display = "none";
    clearPlanCurrentStepIndex();
    $messages.innerHTML =
      '<div class="welcome"><p class="welcome-title">你好，我是 Office Copilot 👋</p><p class="welcome-sub">在此与 AI 对话，可操作当前 Word/Excel 文档。配置请在 Chrome 扩展中完成。</p></div>';
    if ($input) $input.value = "";
    if (ws) {
      try {
        ws.close();
      } catch (e2) { /* ignore */ }
      ws = null;
    }
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    reconnectDelay = RECONNECT_BASE_MS;
    connect();
  }

  function officePptParseReorder1Based(newOrderStr, n) {
    if (!newOrderStr || !String(newOrderStr).trim()) return { err: "[错误] 请提供 newOrder，如 2,3,1。" };
    var parts = String(newOrderStr)
      .split(",")
      .map(function (x) {
        return x.trim();
      })
      .filter(Boolean);
    var order = parts.map(function (p) {
      return parseInt(p, 10);
    });
    for (var i = 0; i < order.length; i++) {
      if (isNaN(order[i])) return { err: "[错误] newOrder 中含无法解析的序号。" };
    }
    if (order.length !== n) return { err: "[错误] newOrder 长度须等于当前幻灯片张数（" + n + "）。" };
    var seen = {};
    for (var j = 0; j < order.length; j++) {
      var x = order[j];
      if (x < 1 || x > n) return { err: "[错误] newOrder 中序号须在 1～" + n + " 之间。" };
      if (seen[x]) return { err: "[错误] newOrder 中序号须无重复。" };
      seen[x] = true;
    }
    return { order: order };
  }

  function officePptParseRowsCsv(rowsCsv) {
    if (!rowsCsv || !String(rowsCsv).trim()) return { err: "[错误] 请提供 rowsCsv。" };
    var lines = String(rowsCsv)
      .split("|")
      .map(function (x) {
        return x.trim();
      })
      .filter(function (x) {
        return x.length > 0;
      });
    var rows = lines.map(function (line) {
      return line.split(",").map(function (cell) {
        return String(cell !== undefined ? cell : "").trim();
      });
    });
    return { rows: rows };
  }

  let currentBotMessageRaw = "";
  let currentRoundWrapper = null;
  let timelineRoot = null;
  let openPrepSeg = null;
  let openThinkSeg = null;
  let openDigestSeg = null;
  let openIntentSeg = null;
  let openAnswerSeg = null;
  const TIMELINE_TAIL_MAX = 100;
  let currentRoundToolBlocks = [];
  let currentToolEndIndex = 0;

  function formatActivityTail(log, maxChars) {
    const s = log || "";
    if (s.length <= maxChars) return s;
    return "…" + s.slice(s.length - maxChars);
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
      const id = "mermaid-" + Date.now() + "-" + index;
      const code = block.textContent;
      const container = document.createElement("div");
      container.className = "mermaid-container";
      container.id = id;
      block.parentNode.replaceWith(container);
      mermaid.render(id + "-svg", code).then((result) => {
        container.innerHTML = result.svg;
      }).catch((err) => {
        container.innerHTML = "<pre>Mermaid Error: " + err.message + "</pre>";
      });
    });
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
    openPrepSeg.tail.textContent = formatActivityTail(pre.textContent, TIMELINE_TAIL_MAX);
    openPrepSeg.details.title = pre.textContent;
    $messages.scrollTop = $messages.scrollHeight;
  }

  function appendAgentTrace(msg) {
    const title =
      ((msg.traceTitle && String(msg.traceTitle).trim()) || (msg.content && String(msg.content).trim()) || "");
    const detail = (msg.traceDetail && String(msg.traceDetail).trim()) || "";
    const cat = (msg.traceCategory && String(msg.traceCategory).trim()) || "trace";
    if (!title && !detail) return;
    let block = `[${cat}] ${title || "(无标题)"}`;
    if (detail) block += `\n${detail}`;
    appendAgentStatusLine(block);
  }

  function appendReasoningChunk(text) {
    const t = text != null ? String(text) : "";
    if (!t) return;
    if (!currentRoundWrapper) beginStream();
    if (!openThinkSeg) openThinkSeg = newTimelineSeg("think", "推理");
    openThinkSeg.pre.textContent += t;
    openThinkSeg.tail.textContent = formatActivityTail(openThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
    openThinkSeg.details.title = openThinkSeg.pre.textContent;
    $messages.scrollTop = $messages.scrollHeight;
  }

  function beginStream() {
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

  function updateExecutionLogCount() {}

  function appendStreamWarning(text) {
    if (!currentRoundWrapper) beginStream();
    const wrap = currentRoundWrapper;
    if (!wrap) return;
    const notice = document.createElement("div");
    notice.className = "msg msg--stream-warning";
    notice.textContent = (text && String(text).trim()) || "服务端返回了警告";
    wrap.insertBefore(notice, wrap.firstChild);
    $messages.scrollTop = $messages.scrollHeight;
  }

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
      console.warn("marked.parse failed", e);
      el.textContent = raw;
    }
  }

  let _streamRenderPending = false;
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
    requestAnimationFrame(() => {
      _streamRenderPending = false;
      if (!openAnswerSeg) return;
      applyMarkedToElement(openAnswerSeg.body, openAnswerSeg.rawMd);
      const plain = openAnswerSeg.rawMd.replace(/\s+/g, " ").trim();
      openAnswerSeg.tail.textContent = formatActivityTail(plain, TIMELINE_TAIL_MAX);
      openAnswerSeg.details.title = plain.slice(0, 200);
      $messages.scrollTop = $messages.scrollHeight;
    });
  }

  function finalizeStream() {
    collapseAllOpenPhases();
    openAnswerSeg = null;
    if (timelineRoot) {
      timelineRoot.querySelectorAll(".timeline-seg--answer").forEach((el) => {
        const div = el.querySelector(".timeline-seg__body--md");
        const raw = el.dataset.streamRaw;
        if (div && raw != null && typeof marked !== "undefined") {
          applyMarkedToElement(div, raw);
        }
      });
      if (typeof mermaid !== "undefined") runMermaidInTimeline(timelineRoot);
      const allD = timelineRoot.querySelectorAll("details");
      for (let di = 0; di < allD.length; di++) allD[di].open = false;
      const ans = timelineRoot.querySelectorAll(".timeline-seg.timeline-seg--answer");
      if (ans.length) ans[ans.length - 1].open = true;
    }
    currentBotMessageRaw = "";
    if (currentRoundToolBlocks.length > 0) {
      currentRoundToolBlocks.forEach(function (b) { b.open = false; });
    }
    currentRoundWrapper = null;
    timelineRoot = null;
    currentRoundToolBlocks = [];
    currentToolEndIndex = 0;
    setInputEnabled(true);
    if (crossAgentAutoRunLock) {
      crossAgentAutoRunLock = false;
    }
    if (crossAgentAutoRunQueued && !currentRoundWrapper) {
      crossAgentAutoRunQueued = false;
      scheduleCrossAgentAutoRun();
    }
  }

  function setInputEnabled(enabled) {
    $input.disabled = !enabled;
    $sendBtn.disabled = !enabled;
    if ($stopBtn) $stopBtn.style.display = enabled ? "none" : "flex";
    if ($sendBtn) $sendBtn.style.display = enabled ? "flex" : "none";
    if (enabled) $input.focus();
  }

  function connect() {
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;

    sessionId = getSessionId();
    ensureBootstrapAuthToken().then(function () {
    var tok = getStoredAuthToken();
    var url = WS_URL + "?sessionId=" + encodeURIComponent(sessionId) + "&clientType=" + encodeURIComponent(OFFICE_CLIENT_TYPE) + (tok ? "&token=" + encodeURIComponent(tok) : "");
    ws = new WebSocket(url);

    ws.onopen = function () {
      reconnectDelay = RECONNECT_BASE_MS;
      setStatus(true);
      addSystemMessage("已连接到本地服务");
      while (pendingMessages.length > 0) {
        const m = pendingMessages.shift();
        send(m.text, { attachments: m.attachmentsPayload || null });
      }
      flushCrossAgentAutoRunAfterReconnect();
    };

    ws.onmessage = function (e) { handleMessage(e.data); };

    ws.onclose = function () {
      const wasStreaming = !!currentRoundWrapper;
      ws = null;
      setStatus(false);
      finalizeStream();
      if (wasStreaming) addBotMessage("连接已断开，请检查网络或稍后重试。", true);
      scheduleReconnect();
    };

    ws.onerror = function () { ws.close(); };
    }).catch(function (err) {
      addSystemMessage("找不到本机 Office Copilot：" + (err && err.message ? err.message : String(err)));
    });
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(function () {
      reconnectTimer = null;
      reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
      connect();
    }, reconnectDelay);
  }

  function send(text, sendOpts) {
    sendOpts = sendOpts || {};
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage("连接已断开，消息未发送。请检查后台是否启动，并在 Chrome 扩展中配置。", true);
      return;
    }
    const payload = { type: "text", content: text || "" };
    const skipPlan = sendOpts.skipPlan === true;
    const attachmentsPayload = sendOpts.attachments || null;
    if (!skipPlan && currentPlanId) {
      payload.mode = "agent";
      payload.planId = currentPlanId;
      payload.planCurrentStepIndex = getPlanCurrentStepIndex();
    }
    if (attachmentsPayload && attachmentsPayload.length > 0) {
      payload.attachments = attachmentsPayload;
    }
    ws.send(JSON.stringify(payload));
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
    send(CROSS_AGENT_AUTO_TRIGGER_TEXT, { skipPlan: true, attachments: null });
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

  async function fetchPlanAndShow(planId, title, createdBy) {
    if (createdBy !== OFFICE_CLIENT_TYPE) return;
    try {
      await tasklyEnsureOfficeApiBase();
      const res = await tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
      if (!res.ok) return;
      const data = await res.json();
      currentPlanId = planId;
      currentPlanTitle = title || data.meta?.title || planId;
      currentPlanContent = data.content || "";
      currentPlanCreatedBy = data.meta?.createdBy || createdBy;
      if ($planPanelTitle) $planPanelTitle.textContent = currentPlanTitle;
      if ($planContentView) {
        $planContentView.innerHTML = (typeof marked !== "undefined") ? marked.parse(currentPlanContent || "") : escapeHtml(currentPlanContent || "");
        $planContentView.style.display = "block";
      }
      if ($planContentEdit) {
        $planContentEdit.value = currentPlanContent || "";
        $planContentEdit.style.display = "none";
      }
      if ($planPanel) $planPanel.style.display = "flex";
      if ($planEditBtn) $planEditBtn.style.display = "inline-block";
      if ($planSaveBtn) $planSaveBtn.style.display = "none";
      if ($planCancelEditBtn) $planCancelEditBtn.style.display = "none";
      initPlanChecklistFromContent(currentPlanContent || "");
    } catch (e) {
      console.error("fetch plan failed", e);
    }
  }

  function showPlanEdit() {
    if (!$planContentView || !$planContentEdit) return;
    $planContentView.style.display = "none";
    $planContentEdit.value = currentPlanContent || "";
    $planContentEdit.style.display = "block";
    if ($planEditBtn) $planEditBtn.style.display = "none";
    if ($planSaveBtn) $planSaveBtn.style.display = "inline-block";
    if ($planCancelEditBtn) $planCancelEditBtn.style.display = "inline-block";
  }

  function cancelPlanEdit() {
    if (!$planContentView || !$planContentEdit) return;
    $planContentEdit.style.display = "none";
    $planContentView.style.display = "block";
    if ($planEditBtn) $planEditBtn.style.display = "inline-block";
    if ($planSaveBtn) $planSaveBtn.style.display = "none";
    if ($planCancelEditBtn) $planCancelEditBtn.style.display = "none";
  }

  function handleMessage(raw) {
    let msg;
    try {
      msg = JSON.parse(raw);
    } catch (e) {
      msg = { type: "text", content: raw };
    }

    switch (msg.type) {
      case "ui_theme_changed": {
        const tid = (msg.uiThemeId && String(msg.uiThemeId).trim()) || "";
        if (tid && typeof TasklyTheme !== "undefined") TasklyTheme.setTheme(tid);
        if (typeof window.tasklyOfficeRefreshEmbedThemes === "function") window.tasklyOfficeRefreshEmbedThemes();
        break;
      }
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
          openIntentSeg.tail.textContent = formatActivityTail(c, TIMELINE_TAIL_MAX);
        } else if (phase === "digest") {
          if (openDigestSeg) {
            openDigestSeg.details.open = false;
            openDigestSeg = null;
          }
          openDigestSeg = newTimelineSeg("digest", "处理工具结果");
          openDigestSeg.pre.textContent = c;
          openDigestSeg.tail.textContent = formatActivityTail(c, TIMELINE_TAIL_MAX);
        }
        $messages.scrollTop = $messages.scrollHeight;
        break;
      }
      case "stream_chunk":
        appendStreamChunk(msg.content);
        break;
      case "stream_warning":
        appendStreamWarning(msg.content);
        break;
      case "stream_end":
        finalizeStream();
        break;
      case "subtask_start": {
        if (!currentRoundWrapper) beginStream();
        const taskDesc = (msg.taskDescription && String(msg.taskDescription).trim()) || "子任务";
        const titleLen = 48;
        const summaryLabel = taskDesc.length <= titleLen ? taskDesc : taskDesc.slice(0, titleLen) + "…";
        appendAgentStatusLine("子代理：" + summaryLabel);
        break;
      }
      case "subtask_chunk":
      case "subtask_end":
        break;
      case "tool_invocation_start": {
        if (msg.planStepIndex) {
          updateChecklistStep(msg.planStepIndex, "in_progress");
        }
        collapseAllOpenPhases();
        ensureTimeline();
        if (!timelineRoot) break;
        const label = msg.summary || "正在执行: " + (msg.plugin || "") + "." + (msg.function || "");
        const block = document.createElement("details");
        block.className = "tool-call-block tool-call--running";
        block.dataset.label = label;
        const sum = document.createElement("summary");
        sum.innerHTML = "<span class=\"tool-status-icon\">⏳</span> " + escapeHtml(label);
        block.appendChild(sum);
        const out = document.createElement("pre");
        out.className = "tool-call-output";
        block.appendChild(out);
        timelineRoot.appendChild(block);
        currentRoundToolBlocks.push(block);
        block.open = true;
        updateExecutionLogCount();
        break;
      }
      case "tool_invocation_end": {
        const block = currentRoundToolBlocks[currentToolEndIndex];
        if (block) {
          const ok = msg.success === true;
          const name = (msg.plugin || "") + "." + (msg.function || "");
          const content = (msg.content && String(msg.content).trim()) || "";
          const displayLabel = (block.dataset.label || name).replace(/^正在执行:\s*/i, "");
          block.classList.remove("tool-call--running");
          block.classList.add(ok ? "tool-call--done" : "tool-call--fail");
          const sum = block.querySelector("summary");
          if (sum) sum.innerHTML = "<span class=\"tool-status-icon\">" + (ok ? "✓" : "✗") + "</span> " + escapeHtml(displayLabel);
          const out = block.querySelector(".tool-call-output");
          if (out) {
            out.textContent = content || "";
            out.style.display = content ? "block" : "none";
          }
        }
        if (msg.planStepIndex) {
          updateChecklistStep(msg.planStepIndex, msg.success === true ? "done" : "pending");
        }
        if (
          msg.plugin === "Plan" &&
          msg.function === "execute_plan_step" &&
          msg.planStepIndex &&
          msg.success === true
        ) {
          setPlanCurrentStepIndex(msg.planStepIndex + 1);
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
        addBotMessage((msg.content && String(msg.content).trim()) || "请求失败，请稍后重试。", true);
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
        const createdBy = (msg.createdBy || "").toLowerCase();
        if (planId && createdBy === OFFICE_CLIENT_TYPE) fetchPlanAndShow(planId, title, createdBy);
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

  if ($planExecuteBtn) {
    $planExecuteBtn.addEventListener("click", function () {
      if (currentPlanId) send("请按当前绑定的计划执行");
    });
  }
  if ($planEditBtn) $planEditBtn.addEventListener("click", showPlanEdit);
  if ($planCancelEditBtn) $planCancelEditBtn.addEventListener("click", cancelPlanEdit);
  if ($planSaveBtn && $planContentEdit) {
    $planSaveBtn.addEventListener("click", async function () {
      if (!currentPlanId) return;
      const content = $planContentEdit.value;
      try {
        await tasklyEnsureOfficeApiBase();
        const res = await tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(currentPlanId), {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ content })
        });
        if (!res.ok) {
          const data = await res.json().catch(() => ({}));
          throw new Error(data.message || "保存失败");
        }
        currentPlanContent = content;
        if ($planContentView) {
          $planContentView.innerHTML = (typeof marked !== "undefined") ? marked.parse(content || "") : escapeHtml(content || "");
        }
        initPlanChecklistFromContent(content || "");
        cancelPlanEdit();
      } catch (e) {
        console.error("save plan failed", e);
        alert(e.message || "保存失败");
      }
    });
  }

  // 预定义脚本注册表：仅执行已注册的 scriptId，供 run_document_script RPC 使用
  const DOCUMENT_SCRIPTS = {
    // 示例：读选区文本（Word 端可复用 word_read_selection 工具，此处仅作扩展示例）
    word_read_selection: function (p) {
      if (typeof Word === "undefined") return Promise.resolve("当前环境不是 Word，无法执行。");
      return Word.run(function (context) {
        const selection = context.document.getSelection();
        selection.load("text");
        return context.sync().then(function () { return selection.text || "(无选区)"; });
      });
    }
  };

  async function handleRpcRequest(msg) {
    const id = msg.id;
    const method = msg.method;
    const params = msg.params || {};
    if (!id || !method) return;

    try {
      let result = null;
      if (method === "run_document_script") {
        const scriptId = params.scriptId;
        const scriptParams = params.scriptParams || {};
        if (!scriptId || typeof DOCUMENT_SCRIPTS[scriptId] !== "function") {
          throw new Error("未知或未注册的脚本 ID: " + (scriptId || ""));
        }
        result = await DOCUMENT_SCRIPTS[scriptId](scriptParams);
        if (typeof result !== "string") result = JSON.stringify(result);
      } else if (method === "run_custom_document_script") {
        const scriptCode = params.scriptCode;
        if (typeof scriptCode !== "string" || !scriptCode.trim()) {
          throw new Error("run_custom_document_script 需要非空的 scriptCode 参数。");
        }
        try {
          const fn = new Function(scriptCode.trim());
          const out = fn();
          result = (out && typeof out.then === "function") ? await out : out;
          if (result !== undefined && result !== null && typeof result !== "string") result = JSON.stringify(result);
          if (result === undefined || result === null) result = "";
        } catch (e) {
          throw new Error(e && e.message ? e.message : String(e));
        }
      } else if (OFFICE_CLIENT_TYPE === "office-word") {
        if (method === "word_insert_text") {
          const text = params.text != null ? String(params.text) : "";
          const style = params.style || null;
          await Word.run(function (context) {
            const body = context.document.body;
            const para = body.insertParagraph(text, "End");
            if (style) {
              var styleMap = {
                "heading1": Word.BuiltInStyleName.heading1,
                "heading2": Word.BuiltInStyleName.heading2,
                "heading3": Word.BuiltInStyleName.heading3,
                "normal": Word.BuiltInStyleName.normal,
                "title": Word.BuiltInStyleName.title,
                "subtitle": Word.BuiltInStyleName.subtitle
              };
              var builtIn = styleMap[(style || "").toLowerCase()];
              if (builtIn) para.styleBuiltIn = builtIn;
            }
            return context.sync();
          });
          result = "成功：已在当前 Word 文档末尾插入内容。";
        } else if (method === "word_read_body") {
          const maxLength = params.maxLength > 0 ? params.maxLength : 8000;
          await Word.run(function (context) {
            const range = context.document.body.getRange();
            range.load("text");
            return context.sync().then(function () {
              let t = range.text || "";
              if (t.length > maxLength) t = t.slice(0, maxLength) + "\n...(已截断)";
              return t || "(无正文)";
            });
          }).then(function (text) { result = text; });
        } else if (method === "word_read_selection") {
          await Word.run(function (context) {
            const selection = context.document.getSelection();
            selection.load("text");
            return context.sync().then(function () {
              return selection.text || "(无选区)";
            });
          }).then(function (text) { result = text; });
        } else if (method === "word_insert_table") {
          const rowCount = Math.max(1, parseInt(params.rowCount, 10) || 1);
          const columnCount = Math.max(1, parseInt(params.columnCount, 10) || 1);
          const values = params.values;
          const insertLocation = params.insertLocation || "End";
          await Word.run(function (context) {
            const body = context.document.body;
            const table = body.insertTable(rowCount, columnCount, insertLocation, values || null);
            try {
              table.styleBuiltIn = Word.BuiltInStyleName.gridTable4_Accent1;
              table.headerRowCount = 1;
            } catch (e) { /* style may not be available in all versions */ }
            return context.sync();
          });
          result = "成功：已在文档中插入 " + rowCount + "×" + columnCount + " 表格。";
        } else if (method === "word_search_replace") {
          const searchText = params.searchText != null ? String(params.searchText) : "";
          const replaceText = params.replaceText != null ? String(params.replaceText) : "";
          const replaceAll = params.replaceAll !== false;
          if (!searchText) throw new Error("searchText 不能为空");
          await Word.run(function (context) {
            const range = context.document.body.getRange();
            range.load("text");
            return context.sync().then(function () {
              let t = range.text || "";
              const newText = replaceAll ? t.split(searchText).join(replaceText) : t.replace(searchText, replaceText);
              range.insertText(newText, "Replace");
              return context.sync();
            });
          });
          result = "成功：已完成查找替换。";
        } else {
          throw new Error("Method not supported in Word: " + method);
        }
      } else if (OFFICE_CLIENT_TYPE === "office-excel") {
        if (method === "excel_read_range") {
          const sheetName = params.sheetName || null;
          const address = params.address || params.range || "A1";
          await Excel.run(function (context) {
            const sheet = sheetName
              ? context.workbook.worksheets.getItem(sheetName)
              : context.workbook.worksheets.getActiveWorksheet();
            const range = sheet.getRange(address);
            range.load("values", "text");
            return context.sync().then(function () {
              return JSON.stringify({ values: range.values, text: range.text });
            });
          }).then(function (out) { result = out; });
        } else if (method === "excel_write_range") {
          const sheetName = params.sheetName || null;
          const address = params.address || params.range || "A1";
          const values = params.values;
          if (!values || !Array.isArray(values)) throw new Error("缺少 values 数组");
          await Excel.run(function (context) {
            const sheet = sheetName
              ? context.workbook.worksheets.getItem(sheetName)
              : context.workbook.worksheets.getActiveWorksheet();
            const range = sheet.getRange(address);
            range.values = values;
            return context.sync();
          });
          result = "成功：已写入当前 Excel 区域。";
        } else if (method === "excel_list_sheets") {
          await Excel.run(function (context) {
            const sheets = context.workbook.worksheets;
            sheets.load("items/name");
            return context.sync().then(function () {
              const names = sheets.items.map(function (s) { return s.name; });
              return JSON.stringify({ names: names });
            });
          }).then(function (out) { result = out; });
        } else if (method === "excel_get_used_range") {
          const sheetName = params.sheetName || null;
          await Excel.run(function (context) {
            const sheet = sheetName
              ? context.workbook.worksheets.getItem(sheetName)
              : context.workbook.worksheets.getActiveWorksheet();
            const usedRange = sheet.getUsedRange();
            usedRange.load("address", "values");
            return context.sync().then(function () {
              return JSON.stringify({ address: usedRange.address, values: usedRange.values });
            });
          }).then(function (out) { result = out; });
        } else if (method === "excel_read_formulas") {
          const sheetName = params.sheetName || null;
          const address = params.address || params.range || "A1";
          await Excel.run(function (context) {
            const sheet = sheetName
              ? context.workbook.worksheets.getItem(sheetName)
              : context.workbook.worksheets.getActiveWorksheet();
            const range = sheet.getRange(address);
            range.load("formulas");
            return context.sync().then(function () {
              return JSON.stringify({ formulas: range.formulas });
            });
          }).then(function (out) { result = out; });
        } else if (method === "excel_write_formulas") {
          const sheetName = params.sheetName || null;
          const address = params.address || params.range || "A1";
          const formulas = params.formulas;
          if (!formulas || !Array.isArray(formulas)) throw new Error("缺少 formulas 数组");
          await Excel.run(function (context) {
            const sheet = sheetName
              ? context.workbook.worksheets.getItem(sheetName)
              : context.workbook.worksheets.getActiveWorksheet();
            const range = sheet.getRange(address);
            range.formulas = formulas;
            return context.sync();
          });
          result = "成功：已写入公式。";
        } else {
          throw new Error("Method not supported in Excel: " + method);
        }
      } else if (OFFICE_CLIENT_TYPE === "office-powerpoint") {
        if (method === "ppt_slides_list") {
          await PowerPoint.run(function (context) {
            var countResult = context.presentation.slides.getCount();
            return context.sync().then(function () {
              var n = countResult.value;
              var slideRefs = [];
              for (var i = 0; i < n; i++) {
                slideRefs.push(context.presentation.slides.getItemAt(i));
              }
              for (var j = 0; j < slideRefs.length; j++) {
                slideRefs[j].load("shapes/items/textFrame/textRange/text");
              }
              return context.sync().then(function () {
                var out = "共 " + n + " 张幻灯片（按播放顺序）：\n";
                for (var k = 0; k < slideRefs.length; k++) {
                  var s = slideRefs[k];
                  var preview = "(无文本)";
                  if (s.shapes && s.shapes.items && s.shapes.items.length > 0) {
                    var parts = [];
                    for (var t = 0; t < s.shapes.items.length; t++) {
                      var sh = s.shapes.items[t];
                      if (sh.textFrame && sh.textFrame.textRange && typeof sh.textFrame.textRange.text === "string") {
                        var txt = sh.textFrame.textRange.text.slice(0, 80);
                        if (txt.length === 80) txt += "...";
                        parts.push(txt);
                      }
                    }
                    if (parts.length > 0) preview = parts.join(" ");
                  }
                  out += "  " + (k + 1) + ". " + preview + "\n";
                }
                return out.trim();
              });
            });
          }).then(function (text) { result = text; });
        } else if (method === "ppt_slide_read") {
          var slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1;
          var includeShapeDetails = params.includeShapeDetails !== false && params.includeShapeDetails !== "false";
          if (slideIndex < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (slideIndex > countResult.value) {
                  result = "[错误] 幻灯片序号 " + slideIndex + " 超出范围，当前共 " + countResult.value + " 张。";
                  return Promise.resolve();
                }
                var slide = context.presentation.slides.getItemAt(slideIndex - 1);
                slide.load("shapes/items/name, textFrame/textRange/text");
                return context.sync().then(function () {
                  var parts = [];
                  var shapeLines = [];
                  if (slide.shapes && slide.shapes.items) {
                    for (var i = 0; i < slide.shapes.items.length; i++) {
                      var sh = slide.shapes.items[i];
                      if (sh.textFrame && sh.textFrame.textRange && typeof sh.textFrame.textRange.text === "string") {
                        var t = sh.textFrame.textRange.text;
                        parts.push(t);
                        if (includeShapeDetails) {
                          var nm = (sh.name != null ? String(sh.name) : "");
                          var pv = t.length > 120 ? t.slice(0, 120) + "..." : t;
                          if (!pv) pv = "(空)";
                          shapeLines.push("  [" + (i + 1) + "] Name=\"" + nm + "\" 预览: " + pv);
                        }
                      }
                    }
                  }
                  var body = "[幻灯片 " + slideIndex + "]\n" + (parts.length > 0 ? parts.join(" ").trim() : "(无文本)");
                  if (includeShapeDetails) {
                    body += "\n\n[形状列表（编号供 shapeIndex）]\n";
                    body += (shapeLines.length > 0 ? shapeLines.join("\n") : "（本页无带文本的形状）");
                  }
                  result = body;
                });
              });
            });
          }
        } else if (method === "ppt_slide_write") {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1;
          var placeholderType = (params.placeholderType || "title").toString().trim().toLowerCase();
          var text = (params.text != null ? params.text : "").toString();
          var shapeIndex = params.shapeIndex != null ? parseInt(params.shapeIndex, 10) : 0;
          var shapeName = (params.shapeName != null ? params.shapeName : "").toString().trim();
          if (slideIndex < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (slideIndex > countResult.value) {
                  result = "[错误] 幻灯片序号 " + slideIndex + " 超出范围。";
                  return Promise.resolve();
                }
                var slide = context.presentation.slides.getItemAt(slideIndex - 1);
                slide.load("shapes/items/name, textFrame/textRange/text");
                return context.sync().then(function () {
                  var items = slide.shapes && slide.shapes.items ? slide.shapes.items : [];
                  var shape = null;
                  if (shapeIndex > 0 && shapeIndex <= items.length) {
                    var cand = items[shapeIndex - 1];
                    if (cand && cand.textFrame && cand.textFrame.textRange) shape = cand;
                  }
                  if (!shape && shapeName) {
                    for (var si = 0; si < items.length; si++) {
                      var it = items[si];
                      if (it.name && String(it.name).toLowerCase() === shapeName.toLowerCase() && it.textFrame && it.textFrame.textRange) {
                        shape = it;
                        break;
                      }
                    }
                  }
                  if (!shape) {
                    var idx = 0;
                    if (placeholderType === "body" || placeholderType === "subtitle") idx = 1;
                    else if (placeholderType === "ctrtitle" || placeholderType === "centeredtitle" || placeholderType === "center") idx = 0;
                    shape = items[idx] || items[0];
                  }
                  if (!shape || !shape.textFrame || !shape.textFrame.textRange) {
                    result = "[错误] 未找到可写入的形状，请用 ppt_slide_read 查看形状编号后传 shapeIndex。";
                    return Promise.resolve();
                  }
                  shape.textFrame.textRange.text = text;
                  return context.sync().then(function () { result = "成功：已写入幻灯片文本。"; });
                });
              });
            });
          }
        } else if (method === "ppt_slide_insert") {
          var position = params.position != null ? parseInt(params.position, 10) : null;
          var titleText = (params.titleText != null ? params.titleText : "").toString();
          var bodyText = (params.bodyText != null ? params.bodyText : "").toString();
          await PowerPoint.run(function (context) {
            context.presentation.slides.add();
            return context.sync().then(function () {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                var newSlide = context.presentation.slides.getItemAt(countResult.value - 1);
                newSlide.load("shapes/items/textFrame/textRange/text");
                return context.sync().then(function () {
                  var items = newSlide.shapes && newSlide.shapes.items ? newSlide.shapes.items : [];
                  if (items[0] && items[0].textFrame && items[0].textFrame.textRange) items[0].textFrame.textRange.text = titleText;
                  if (items[1] && items[1].textFrame && items[1].textFrame.textRange) items[1].textFrame.textRange.text = bodyText;
                  return context.sync().then(function () { result = "成功：已插入新幻灯片。"; });
                });
              });
            });
          });
        } else if (method === "ppt_slide_delete") {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 0;
          if (slideIndex < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (slideIndex > countResult.value) {
                  result = "[错误] 幻灯片序号 " + slideIndex + " 超出范围。";
                  return Promise.resolve();
                }
                var slide = context.presentation.slides.getItemAt(slideIndex - 1);
                slide.delete();
                return context.sync().then(function () { result = "成功：已删除该幻灯片。"; });
              });
            });
          }
        } else if (method === "ppt_document_create") {
          var createPath = params.filePath != null ? String(params.filePath).trim() : "";
          if (!createPath) {
            result = "[错误] 请提供 filePath（.pptx 或 .pptm）。";
          } else {
            var lp = createPath.toLowerCase();
            if (!lp.endsWith(".pptx") && !lp.endsWith(".pptm")) {
              result = "[错误] filePath 须为 .pptx 或 .pptm。";
            } else if (typeof PowerPoint !== "undefined" && typeof PowerPoint.createPresentation === "function") {
              PowerPoint.createPresentation();
              result =
                "成功：已调用 PowerPoint.createPresentation 打开新演示文稿。Office.js 无法将新文件直接保存到磁盘路径；若需保存到 " +
                createPath +
                "，请在 PowerPoint 中手动「另存为」。";
            } else {
              result = "[错误] 当前宿主未提供 PowerPoint.createPresentation，无法新建演示文稿。";
            }
          }
        } else if (method === "ppt_slide_image_add") {
          var imgSlideIdx = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1;
          var b64 = params.imageBase64 != null ? String(params.imageBase64).trim() : "";
          if (b64.indexOf("base64,") >= 0) b64 = b64.split("base64,").pop() || "";
          b64 = b64.replace(/\s/g, "");
          if (!b64) {
            result =
              "[错误] PowerPoint 任务窗格无法从本机路径读取图片；后台未成功读取 imagePath 或未附带 imageBase64。请确认后台与 PowerPoint 同机且图片路径可读，或使用 Chrome + 文件路径调用 ppt_slide_image_add。";
          } else if (imgSlideIdx < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (imgSlideIdx > countResult.value) {
                  result = "[错误] 幻灯片序号 " + imgSlideIdx + " 超出范围，当前共 " + countResult.value + " 张。";
                  return Promise.resolve();
                }
                var slide = context.presentation.slides.getItemAt(imgSlideIdx - 1);
                slide.shapes.addPicture(b64, {
                  left: 20,
                  top: 120,
                  width: 400,
                  height: 225,
                });
                return context.sync().then(function () {
                  result = "成功：已在第 " + imgSlideIdx + " 页插入图片。";
                });
              });
            });
          }
        } else if (method === "ppt_notes_read" || method === "ppt_notes_write") {
          result =
            "[错误] PowerPoint JavaScript API（任务窗格）当前不支持演讲者备注读写。请使用 WPS 演示任务窗格，或使用 Chrome 扩展 + 后端文件路径调用 " +
            method +
            "。";
        } else if (method === "ppt_slides_reorder") {
          var ordStr = params.newOrder != null ? String(params.newOrder).trim() : "";
          await PowerPoint.run(function (context) {
            var countResult = context.presentation.slides.getCount();
            return context.sync().then(function () {
              var nn = countResult.value;
              var parsedOrd = officePptParseReorder1Based(ordStr, nn);
              if (parsedOrd.err) {
                result = parsedOrd.err;
                return Promise.resolve();
              }
              var ord = parsedOrd.order;
              var refs = [];
              for (var ki = 0; ki < ord.length; ki++) {
                refs.push(context.presentation.slides.getItemAt(ord[ki] - 1));
              }
              for (var ti = 0; ti < nn; ti++) {
                refs[ti].moveTo(ti);
              }
              return context.sync().then(function () {
                result = "成功：已重排幻灯片。";
              });
            });
          });
        } else if (method === "ppt_table_create") {
          var tSlideIdx = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1;
          var tRows = params.rows != null ? parseInt(params.rows, 10) : 2;
          var tCols = params.cols != null ? parseInt(params.cols, 10) : 2;
          if (tSlideIdx < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else if (tRows < 1 || tCols < 1 || tRows > 20 || tCols > 10) {
            result = "[错误] 表格行列无效（行 1–20，列 1–10）。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (tSlideIdx > countResult.value) {
                  result = "[错误] 幻灯片序号超出范围。";
                  return Promise.resolve();
                }
                var sld = context.presentation.slides.getItemAt(tSlideIdx - 1);
                sld.shapes.addTable(tRows, tCols, { left: 36, top: 216, width: 600, height: 200 });
                return context.sync().then(function () {
                  result = "成功：已在第 " + tSlideIdx + " 页添加表格（" + tRows + "×" + tCols + "）。";
                });
              });
            });
          }
        } else if (method === "ppt_table_write_cells") {
          var wcSlideIdx = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1;
          var wcCsv = params.rowsCsv != null ? params.rowsCsv : "";
          var wcParsed = officePptParseRowsCsv(wcCsv);
          if (wcParsed.err) {
            result = wcParsed.err;
          } else if (wcSlideIdx < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (wcSlideIdx > countResult.value) {
                  result = "[错误] 幻灯片序号超出范围。";
                  return Promise.resolve();
                }
                var sld2 = context.presentation.slides.getItemAt(wcSlideIdx - 1);
                sld2.shapes.load("items/type");
                return context.sync().then(function () {
                  var items = sld2.shapes.items || [];
                  var tableShape = null;
                  for (var qi = 0; qi < items.length; qi++) {
                    var it = items[qi];
                    if (it.type === "Table" || String(it.type).toLowerCase() === "table") {
                      tableShape = it;
                      break;
                    }
                  }
                  if (!tableShape) {
                    result = "[错误] 该幻灯片上未找到表格。";
                    return Promise.resolve();
                  }
                  var tbl = tableShape.getTable();
                  tbl.load("rowCount,columnCount");
                  return context.sync().then(function () {
                    var inRows = wcParsed.rows;
                    for (var r = 0; r < inRows.length; r++) {
                      var cells = inRows[r];
                      for (var c = 0; c < cells.length; c++) {
                        if (r >= tbl.rowCount || c >= tbl.columnCount) continue;
                        var cell = tbl.getCellOrNullObject(r, c);
                        if (!cell.isNullObject) cell.text = cells[c];
                      }
                    }
                    return context.sync().then(function () {
                      result = "成功：已写入表格单元格。";
                    });
                  });
                });
              });
            });
          }
        } else if (method === "ppt_hyperlink_add") {
          var hSlideIdx = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1;
          var hUrl = params.url != null ? String(params.url).trim() : "";
          var hShapeIdx = params.shapeIndex != null ? parseInt(params.shapeIndex, 10) : 1;
          var hShapeName = params.shapeName != null ? String(params.shapeName).trim() : "";
          if (hSlideIdx < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else if (!hUrl) {
            result = "[错误] URL 为空。";
          } else if (!/^https?:\/\//i.test(hUrl)) {
            result = "[错误] URL 必须是绝对地址（如 https://...）。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (hSlideIdx > countResult.value) {
                  result = "[错误] 幻灯片序号超出范围。";
                  return Promise.resolve();
                }
                var hs = context.presentation.slides.getItemAt(hSlideIdx - 1);
                hs.shapes.load("items/name, items/textFrame/textRange/text");
                return context.sync().then(function () {
                  var hItems = hs.shapes.items || [];
                  var hShape = null;
                  if (hShapeIdx > 0 && hShapeIdx <= hItems.length) {
                    var hc = hItems[hShapeIdx - 1];
                    if (hc && hc.textFrame && hc.textFrame.textRange) hShape = hc;
                  }
                  if (!hShape && hShapeName) {
                    for (var hi = 0; hi < hItems.length; hi++) {
                      var hx = hItems[hi];
                      if (hx.name && String(hx.name).toLowerCase() === hShapeName.toLowerCase() && hx.textFrame && hx.textFrame.textRange) {
                        hShape = hx;
                        break;
                      }
                    }
                  }
                  if (!hShape || !hShape.textFrame || !hShape.textFrame.textRange) {
                    result = "[错误] 未找到可设置超链接的文本形状，请检查 shapeIndex 或 shapeName。";
                    return Promise.resolve();
                  }
                  if (!hShape.textFrame.textRange.text || hShape.textFrame.textRange.text.length === 0) {
                    hShape.textFrame.textRange.text = hUrl;
                  }
                  hShape.setHyperlink({ address: hUrl, screenTip: hUrl });
                  return context.sync().then(function () {
                    result = "成功：已添加超链接。";
                  });
                });
              });
            });
          }
        } else if (method === "ppt_slide_duplicate") {
          var dupIdx = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1;
          if (dupIdx < 1) {
            result = "[错误] slideIndex 必须大于等于 1。";
          } else {
            await PowerPoint.run(function (context) {
              var countResult = context.presentation.slides.getCount();
              return context.sync().then(function () {
                if (dupIdx > countResult.value) {
                  result = "[错误] 幻灯片序号超出范围。";
                  return Promise.resolve();
                }
                var dupSlide = context.presentation.slides.getItemAt(dupIdx - 1);
                dupSlide.load("id");
                var b64res = dupSlide.exportAsBase64();
                return context.sync().then(function () {
                  var payload = b64res.value;
                  var fmt =
                    typeof PowerPoint !== "undefined" && PowerPoint.InsertSlideFormatting
                      ? PowerPoint.InsertSlideFormatting.keepSourceFormatting
                      : "KeepSourceFormatting";
                  context.presentation.insertSlidesFromBase64(payload, {
                    targetSlideId: dupSlide.id,
                    formatting: fmt,
                  });
                  return context.sync().then(function () {
                    result = "成功：已复制幻灯片（插入在源页之后）。若含复杂媒体导致失败，请见宿主报错信息。";
                  });
                });
              });
            });
          }
        } else {
          throw new Error("Method not supported in PowerPoint: " + method);
        }
      } else {
        throw new Error("Method not supported in this client: " + method);
      }
      sendRpcResponse(id, result, null);
    } catch (err) {
      console.error("RPC Error:", err);
      sendRpcResponse(id, null, err.message || String(err));
    }
  }

  function sendRpcResponse(id, result, error) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ type: "rpc_response", id: id, result: result != null ? result : null, error: error || null }));
  }

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
    if (!requestId) return;
    pendingConfirmId = requestId;
    if ($hitlAction) $hitlAction.textContent = action;
    if ($hitlAddToListBtn) $hitlAddToListBtn.style.display = (hitlKind === "run_command" || hitlKind === "run_page_script") ? "" : "none";
    if ($hitlOverlay) {
      $hitlOverlay.style.display = "flex";
      $hitlOverlay.setAttribute("aria-hidden", "false");
    }
  }

  function sendConfirmResponse(id, allowed, addToAllowList) {
    if (!id) return;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: "confirm_response", id: id, allowed: allowed, addToAllowList: !!addToAllowList }));
    }
    pendingConfirmId = null;
    if ($hitlOverlay) {
      $hitlOverlay.style.display = "none";
      $hitlOverlay.setAttribute("aria-hidden", "true");
    }
  }

  if ($hitlAllowBtn) $hitlAllowBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true, false); });
  if ($hitlAddToListBtn) $hitlAddToListBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true, true); });
  if ($hitlDenyBtn) $hitlDenyBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, false); });

  function handleSend() {
    if (!$input) return;
    const text = $input.value.trim();
    const hasAttachments = attachments.length > 0;
    if (!text && !hasAttachments) return;
    if (currentRoundWrapper) return;

    const attachmentsPayload = hasAttachments ? buildAttachmentsPayload() : null;
    const userLabel = text || (hasAttachments ? "（附图片）" : "");

    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addUserMessage(userLabel);
      pendingMessages.push({ text: text, attachmentsPayload: attachmentsPayload });
      addBotMessage("连接已断开，正在重连… 请确保后台已启动并在 Chrome 扩展中配置。", true);
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      connect();
      $input.value = "";
      attachments = [];
      renderAttachmentsPreview();
      $input.focus();
      return;
    }

    addUserMessage(userLabel);
    send(text, { attachments: attachmentsPayload });
    $input.value = "";
    attachments = [];
    renderAttachmentsPreview();
    $input.focus();
  }

  if ($newChatBtn) $newChatBtn.addEventListener("click", resetOfficeConversation);
  if ($attachBtn && $fileInput) {
    $attachBtn.addEventListener("click", function () {
      $fileInput.click();
    });
  }
  if ($fileInput) {
    $fileInput.addEventListener("change", function (e) {
      const t = e.target;
      if (!t || !t.files) return;
      addFilesAsAttachments(t.files).then(function () {
        try {
          t.value = "";
        } catch (err) { /* ignore */ }
      });
    });
  }
  if ($atModeList) {
    $atModeList.addEventListener("click", function (e) {
      const item = e.target && e.target.closest ? e.target.closest(".at-mode-item") : null;
      if (!item || item.dataset.atIdx == null) return;
      const idx = parseInt(item.dataset.atIdx, 10);
      if (!isNaN(idx) && atModeTopList[idx]) insertAtCandidate(atModeTopList[idx]);
    });
  }
  if ($sendBtn) $sendBtn.addEventListener("click", handleSend);
  if ($stopBtn) {
    $stopBtn.addEventListener("click", function () {
      if (!currentRoundWrapper) return;
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "stop" }));
      finalizeStream();
    });
  }
  if ($input) {
    $input.addEventListener("keydown", function (e) {
      if (atModeOpen) {
        if (e.key === "Escape") {
          e.preventDefault();
          closeAtMode();
          return;
        }
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
          pickAtModeActive();
          return;
        }
      }
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        handleSend();
      }
    });
    $input.addEventListener("keyup", function () {
      scheduleAtModeSync();
    });
    $input.addEventListener("input", function () {
      scheduleAtModeSync();
    });
  }

  function boot() {
    sessionId = getSessionId();
    if (typeof Office !== "undefined" && Office.context && Office.context.host) {
      var host = Office.context.host;
      if (host === Office.HostType.Word) OFFICE_CLIENT_TYPE = "office-word";
      else if (host === Office.HostType.Excel) OFFICE_CLIENT_TYPE = "office-excel";
      else if (typeof Office.HostType.PowerPoint !== "undefined" && host === Office.HostType.PowerPoint) OFFICE_CLIENT_TYPE = "office-powerpoint";
    }
    connect();
  }

  if (typeof Office !== "undefined") {
    Office.onReady(function () { boot(); });
  } else {
    boot();
  }
})();
