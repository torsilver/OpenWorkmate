(function () {
  "use strict";

  var WS_URL = "ws://127.0.0.1:8765/ws";
  var API_BASE = "http://127.0.0.1:8765";
  var openWorkmateOfficeApiReady = null;
  function openWorkmateEnsureOfficeApiBase() {
    if (openWorkmateOfficeApiReady) return openWorkmateOfficeApiReady;
    openWorkmateOfficeApiReady = OpenWorkmateLocalService.openWorkmateResolveLocalServiceBase(null).then(function (r) {
      var hw = OpenWorkmateLocalService.openWorkmateHttpWsFromBase(r.baseUrl);
      API_BASE = hw.apiBase;
      WS_URL = hw.wsUrl;
    });
    return openWorkmateOfficeApiReady;
  }
  var openWorkmate_AUTH_TOKEN_KEY = "OpenWorkmateLocalServiceAuthToken";
  var openWorkmate_TELEMETRY_DEVICE_ID_KEY = "openWorkmateTelemetryDeviceId";
  var openWorkmate_TELEMETRY_CLIENT_EMISSION_KEY = "openWorkmateTelemetryClientEmission";
  var openWorkmate_TELEMETRY_RELAY_ACTIVE_PROFILE_KEY = "openWorkmateTelemetryRelayActiveProfileId";
  var openWorkmate_TELEMETRY_EVENT_KINDS_BY_PROFILE_KEY = "openWorkmateTelemetryEventKindsByProfile";
  var STORAGE_ACTIVE_AGENT_PROFILE_ID = "activeAgentProfileId";

  function getStoredAuthToken() {
    try { return (localStorage.getItem(openWorkmate_AUTH_TOKEN_KEY) || "").trim(); } catch (e) { return ""; }
  }
  function getStoredAgentProfileId() {
    try {
      return (localStorage.getItem(STORAGE_ACTIVE_AGENT_PROFILE_ID) || "default").trim() || "default";
    } catch (e) {
      return "default";
    }
  }
  function persistAgentProfileId(id) {
    try {
      localStorage.setItem(STORAGE_ACTIVE_AGENT_PROFILE_ID, (id && String(id).trim()) || "default");
    } catch (e) { /* ignore */ }
  }
  function openWorkmateFetch(url, init) {
    init = init || {};
    var headers = new Headers(init.headers || {});
    var t = getStoredAuthToken();
    if (t) headers.set("X-OpenWorkmate-Token", t);
    return fetch(url, Object.assign({}, init, { headers: headers }));
  }
  function ensureBootstrapAuthToken() {
    return openWorkmateEnsureOfficeApiBase().then(function () {
      return fetch(API_BASE + "/api/bootstrap/local-service-auth")
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (j) {
          if (!j || !j.ok) return j;
          try {
            localStorage.setItem(
              openWorkmate_TELEMETRY_CLIENT_EMISSION_KEY,
              j.telemetryUserObservabilityEnabled === false ? "off" : "on"
            );
          } catch (e) { /* ignore */ }
          var t = (j.webSocketAuthToken || "").trim();
          if (t && !getStoredAuthToken()) {
            try {
              localStorage.setItem(openWorkmate_AUTH_TOKEN_KEY, t);
            } catch (e2) { /* ignore */ }
          }
          return j;
        })
        .catch(function () { return null; });
    });
  }
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 16000;

  let OFFICE_CLIENT_TYPE = "office"; // set after Office.onReady to office-word | office-excel | office-powerpoint

  (function openWorkmateSyncThemeFromBackend() {
    try {
      openWorkmateEnsureOfficeApiBase().then(function () {
      openWorkmateFetch(API_BASE + "/api/config")
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (j) {
          if (!j || typeof OpenWorkmateTheme === "undefined") return;
          var id = j.uiThemeId || j.UiThemeId;
          if (id) OpenWorkmateTheme.setTheme(id);
          if (typeof window.openWorkmateOfficeRefreshEmbedThemes === "function") window.openWorkmateOfficeRefreshEmbedThemes();
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
  const $agentProfileSelect = document.getElementById("agent-profile-select");
  const $historyChatBtn = document.getElementById("history-chat-btn");
  const $settingsBtn = document.getElementById("settings-btn");
  const $historyOverlay = document.getElementById("history-overlay");
  const $historyOverlayBackdrop = document.getElementById("history-overlay-backdrop");
  const $historyOverlayClose = document.getElementById("history-overlay-close");
  const $historyList = document.getElementById("history-list");
  const $historyError = document.getElementById("history-error");
  const $historyLoadMore = document.getElementById("history-load-more");

  const STORAGE_PLAN_STEP_INDEX = "copilot_plan_step_index";
  var OFFICE_WELCOME_INNER_HTML =
    '<div class="welcome"><p class="welcome-title">你好，我是 Open Workmate 👋</p><p class="welcome-sub">在此与 AI 对话，可操作当前 Word/Excel/PowerPoint 文档。完整配置请在 Chrome 扩展 options 页完成。</p></div>';
  var lastSetContextSig = "";
  var _suppressOfficeAgentSelectChange = false;

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

  function officeHostKindForSetContext() {
    if (OFFICE_CLIENT_TYPE === "office-word") return "word";
    if (OFFICE_CLIENT_TYPE === "office-excel") return "et";
    if (OFFICE_CLIENT_TYPE === "office-powerpoint") return "wpp";
    return "";
  }

  function getOfficeDocumentLabelPromise() {
    return new Promise(function (resolve) {
      try {
        var host = Office.context.host;
        var appName =
          host === Office.HostType.Word
            ? "Microsoft Word"
            : host === Office.HostType.Excel
              ? "Microsoft Excel"
              : "Microsoft PowerPoint";
        var url = "";
        try {
          if (Office.context.document && Office.context.document.url) url = String(Office.context.document.url);
        } catch (e0) { /* ignore */ }
        var fromUrl = "";
        if (url) {
          try {
            var path = url.split("?")[0];
            fromUrl = path.replace(/\\/g, "/").split("/").pop() || "";
          } catch (e1) { /* ignore */ }
        }
        function finish(name) {
          var n = (name && String(name).trim()) || fromUrl || "未保存文档";
          var line = (appName ? appName + " · " : "") + n;
          if (line.length > 200) line = line.slice(0, 200);
          resolve(line);
        }
        if (host === Office.HostType.Word && typeof Word !== "undefined") {
          Word.run(function (context) {
            var props = context.document.properties;
            props.load("title");
            return context.sync().then(function () {
              var fn = fromUrl;
              try {
                var ti = props.title;
                if (ti && String(ti).trim()) fn = String(ti).trim();
              } catch (e2) { /* ignore */ }
              finish(fn);
            });
          }).catch(function () { finish(fromUrl); });
          return;
        }
        if (host === Office.HostType.Excel && typeof Excel !== "undefined") {
          Excel.run(function (context) {
            context.workbook.load("name");
            return context.sync().then(function () {
              finish(context.workbook.name || fromUrl);
            });
          }).catch(function () { finish(fromUrl); });
          return;
        }
        if (typeof Office.HostType !== "undefined" && Office.HostType.PowerPoint && host === Office.HostType.PowerPoint && typeof PowerPoint !== "undefined") {
          PowerPoint.run(function (context) {
            context.presentation.load("title");
            return context.sync().then(function () {
              finish(context.presentation.title || fromUrl);
            });
          }).catch(function () { finish(fromUrl); });
          return;
        }
        finish(fromUrl || "");
      } catch (e) {
        resolve("");
      }
    });
  }

  function sendOfficeSetContextIfNeeded() {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    getOfficeDocumentLabelPromise().then(function (pageTitle) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      var hk = officeHostKindForSetContext();
      var pt = (pageTitle && String(pageTitle).trim()) || "";
      if (!hk && !pt) return;
      var sig = (hk || "") + "\0" + pt;
      if (sig === lastSetContextSig) return;
      var payload = { type: "set_context" };
      if (hk) payload.wpsHostKind = hk;
      if (pt) payload.pageTitle = pt.length > 200 ? pt.slice(0, 200) : pt;
      try {
        ws.send(JSON.stringify(payload));
        lastSetContextSig = sig;
      } catch (e) { /* ignore */ }
    });
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
    planChecklistLoadedPlanId = currentPlanId || planChecklistLoadedPlanId;
    renderPlanChecklist();
  }

  /** 对齐 chrome-extension/sidepanel.js ensurePlanChecklistLoaded：执行计划某步前确保步骤列表已解析 */
  function ensurePlanChecklistLoaded(planId) {
    return Promise.resolve().then(function () {
      if (!planId) return;
      if (planChecklistSteps.length > 0 && planChecklistLoadedPlanId === planId) return;
      planChecklistLoadedPlanId = planId;
      return openWorkmateEnsureOfficeApiBase()
        .then(function () {
          return openWorkmateFetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
        })
        .then(function (res) {
          if (!res.ok) return null;
          return res.json();
        })
        .then(function (data) {
          if (!data) return;
          var content = data.content || "";
          planChecklistSteps = parsePlanStepsFromContent(content);
          planChecklistStatus = {};
          planChecklistSteps.forEach(function (s) {
            planChecklistStatus[s.index] = "pending";
          });
          renderPlanChecklist();
        })
        .catch(function (e) {
          console.warn("Failed to load plan for checklist", e);
        });
    });
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
    atModeLoadingPromise = openWorkmateEnsureOfficeApiBase().then(function () {
    return openWorkmateFetch(API_BASE + "/api/tools/builtin")
      .then(function (builtinRes) {
        return openWorkmateFetch(API_BASE + "/api/skills").then(function (skillsRes) {
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
    resetContextUsageRingOffice();
    closeAtMode();
    closeOfficeHistoryOverlay();
    closeOfficeAskOptionsOverlay();
    try {
      sessionStorage.removeItem("copilot_session_id");
    } catch (e) { /* ignore */ }
    lastSetContextSig = "";
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
    planChecklistLoadedPlanId = null;
    if ($planChecklistWrap) $planChecklistWrap.style.display = "none";
    clearPlanCurrentStepIndex();
    $messages.innerHTML = OFFICE_WELCOME_INNER_HTML;
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
  /** 与 Chrome 侧栏一致：流式 tool_call_delta 挂在「工具参数（生成中）」时间线段内 */
  let openToolDraftSeg = null;
  let openAnswerSeg = null;
  const TIMELINE_TAIL_MAX = 100;
  let currentRoundToolBlocks = [];
  let currentToolEndIndex = 0;
  /** 与 chrome-extension/sidepanel.js：同一计划执行进度条在未打开计划正文时按需拉取 */
  let planChecklistLoadedPlanId = null;
  /** tool_invocation 耗时刷新定时器 */
  const toolBlockElapsedTimers = new Map();
  /** 子代理（Sub-Agent）时间线：对齐 Chrome / WPS WebSocket 帧 */
  let currentSubtaskBlock = null;
  let currentSubtaskStreamEl = null;
  let currentSubtaskToolsEl = null;
  let currentSubtaskReasoningRoot = null;
  let currentSubtaskToolBlocks = [];
  let currentSubtaskToolEndIndex = 0;
  let openSubtaskToolDraft = null;
  let subtaskThinkCells = new Map();
  let openSubtaskThinkSeg = null;
  let _subtaskReasoningPendingText = "";
  let _subtaskReasoningPendingSeq = null;
  let _subtaskReasoningFlushPending = false;
  let _subtaskReasoningRafId = null;
  let timelineThinkCells = new Map();
  let timelineAnswerCells = new Map();

  function decodeJsonStyleUnicodeEscapes(s) {
    if (typeof openWorkmateHostShared !== "undefined" && openWorkmateHostShared.decodeJsonStyleUnicodeEscapes) {
      return openWorkmateHostShared.decodeJsonStyleUnicodeEscapes(s);
    }
    return s;
  }

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

  /** 对齐 chrome-extension/sidepanel.js：tool_invocation_start 后刷新耗时 */
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

  function finalizeOpenSubtaskToolDraft() {
    if (openSubtaskToolDraft && openSubtaskToolDraft.wrap && openSubtaskToolDraft.wrap.parentNode) {
      try {
        const pre = openSubtaskToolDraft.pre;
        if (pre) pre.textContent = decodeJsonStyleUnicodeEscapes(pre.textContent || "");
      } catch (e) { /* ignore */ }
      openSubtaskToolDraft.wrap.open = false;
      try {
        const sum = openSubtaskToolDraft.wrap.querySelector("summary");
        if (sum && sum.textContent && sum.textContent.indexOf("生成中") !== -1) {
          sum.textContent = sum.textContent.replace("（生成中）", "");
        }
      } catch (e2) { /* ignore */ }
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
    openSubtaskToolDraft = { wrap: wrap, pre: pre, lastCallId: "" };
    return openSubtaskToolDraft;
  }

  function insertSubtaskThinkBlockInOrder(detailsEl, blockSeq) {
    if (!currentSubtaskReasoningRoot) return;
    detailsEl.dataset.blockSeq = String(blockSeq);
    const nodes = currentSubtaskReasoningRoot.children;
    for (let i = 0; i < nodes.length; i++) {
      const node = nodes[i];
      const raw = node.dataset && node.dataset.blockSeq;
      if (raw == null || raw === "") continue;
      const n = parseInt(raw, 10);
      if (Number.isFinite(n) && n > blockSeq) {
        currentSubtaskReasoningRoot.insertBefore(detailsEl, node);
        return;
      }
    }
    currentSubtaskReasoningRoot.appendChild(detailsEl);
  }

  function ensureSubtaskThinkTimelineBlock(blockSeq) {
    let cell = subtaskThinkCells.get(blockSeq);
    if (cell) return cell;
    if (!currentSubtaskReasoningRoot) return null;
    const d = document.createElement("details");
    d.className = "timeline-seg timeline-seg--think timeline-seg--subtask-nested";
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
    insertSubtaskThinkBlockInOrder(d, blockSeq);
    cell = { details: d, pre: pre, tail: tail };
    subtaskThinkCells.set(blockSeq, cell);
    return cell;
  }

  function cancelSubtaskReasoningRaf() {
    if (_subtaskReasoningRafId != null) {
      cancelAnimationFrame(_subtaskReasoningRafId);
      _subtaskReasoningRafId = null;
    }
    _subtaskReasoningFlushPending = false;
  }

  function flushSubtaskReasoningPendingToDom() {
    _subtaskReasoningFlushPending = false;
    _subtaskReasoningRafId = null;
    const buf = _subtaskReasoningPendingText;
    _subtaskReasoningPendingText = "";
    if (!buf || !openSubtaskThinkSeg) return;
    openSubtaskThinkSeg.pre.textContent += buf;
    openSubtaskThinkSeg.tail.textContent = formatActivityTail(openSubtaskThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
    openSubtaskThinkSeg.details.title = openSubtaskThinkSeg.pre.textContent;
    $messages.scrollTop = $messages.scrollHeight;
  }

  function scheduleSubtaskReasoningFlush() {
    if (_subtaskReasoningFlushPending) return;
    _subtaskReasoningFlushPending = true;
    _subtaskReasoningRafId = requestAnimationFrame(flushSubtaskReasoningPendingToDom);
  }

  function flushSubtaskReasoningPendingSync() {
    cancelSubtaskReasoningRaf();
    if (_subtaskReasoningPendingText && openSubtaskThinkSeg) {
      openSubtaskThinkSeg.pre.textContent += _subtaskReasoningPendingText;
      _subtaskReasoningPendingText = "";
      openSubtaskThinkSeg.tail.textContent = formatActivityTail(openSubtaskThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
      openSubtaskThinkSeg.details.title = openSubtaskThinkSeg.pre.textContent;
    } else {
      _subtaskReasoningPendingText = "";
    }
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

  function collapsePhasesForToolStart() {
    collapseSeg(openPrepSeg);
    openPrepSeg = null;
    collapseSeg(openDigestSeg);
    openDigestSeg = null;
    collapseSeg(openIntentSeg);
    openIntentSeg = null;
    closeOpenAnswerSegment();
  }

  function finalizeOpenToolDraftSeg() {
    if (openToolDraftSeg && openToolDraftSeg.details && openToolDraftSeg.details.parentNode) {
      try {
        const pre = openToolDraftSeg.pre;
        if (pre) {
          pre.textContent = decodeJsonStyleUnicodeEscapes(pre.textContent || "");
        }
        const tail = openToolDraftSeg.tail;
        if (tail && pre) {
          tail.textContent = formatActivityTail(pre.textContent || "", TIMELINE_TAIL_MAX);
        }
      } catch (e) { /* ignore */ }
      openToolDraftSeg.details.open = false;
      try {
        const lab = openToolDraftSeg.details.querySelector(".timeline-seg__label");
        if (lab && lab.textContent && lab.textContent.indexOf("生成中") !== -1) {
          lab.textContent = lab.textContent.replace("（生成中）", "");
        }
      } catch (e) { /* ignore */ }
    }
    openToolDraftSeg = null;
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
      let sd = ensureSubtaskToolDraft();
      if (!sd) return;
      const idChanged = sd.lastCallId !== callId;
      if (idChanged && sd.pre.textContent) {
        finalizeOpenSubtaskToolDraft();
        sd = ensureSubtaskToolDraft();
        if (!sd) return;
      }
      if (idChanged) {
        if (sd.pre.textContent) sd.pre.textContent += "\n\n";
        sd.pre.textContent += "[" + callId + "]" + (name.trim() ? " " + name.trim() : "") + "\n";
      }
      sd.lastCallId = callId;
      sd.pre.textContent += delta;
      $messages.scrollTop = $messages.scrollHeight;
      return;
    }
    if (!currentRoundWrapper) beginStream();
    ensureTimeline();
    let d = ensureToolDraftSeg();
    const idChanged = d.lastCallId !== callId;
    if (idChanged && d.pre.textContent) {
      finalizeOpenToolDraftSeg();
      d = ensureToolDraftSeg();
    }
    if (idChanged) {
      if (d.pre.textContent) d.pre.textContent += "\n\n";
      d.pre.textContent += "[" + callId + "]" + (name.trim() ? " " + name.trim() : "") + "\n";
    }
    d.lastCallId = callId;
    d.pre.textContent += delta;
    d.tail.textContent = formatActivityTail(d.pre.textContent, TIMELINE_TAIL_MAX);
    d.details.title = (d.pre.textContent || "").slice(0, 500);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function collapseAllOpenPhases() {
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

  function appendReasoningChunk(text, blockSeq, blockKind, isSubtask) {
    const t = text != null ? String(text) : "";
    if (!t) return;
    if (!currentRoundWrapper) beginStream();
    if (isSubtask === true) {
      if (!currentSubtaskReasoningRoot) return;
      const useBlock =
        typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "think";
      if (useBlock) {
        if (_subtaskReasoningPendingSeq !== null && _subtaskReasoningPendingSeq !== blockSeq)
          flushSubtaskReasoningPendingSync();
        const cell = ensureSubtaskThinkTimelineBlock(blockSeq);
        if (!cell) return;
        openSubtaskThinkSeg = cell;
        _subtaskReasoningPendingSeq = blockSeq;
        _subtaskReasoningPendingText += t;
        scheduleSubtaskReasoningFlush();
        return;
      }
      _subtaskReasoningPendingSeq = null;
      if (!openSubtaskThinkSeg) {
        const d = document.createElement("details");
        d.className = "timeline-seg timeline-seg--think timeline-seg--subtask-nested";
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
        currentSubtaskReasoningRoot.appendChild(d);
        openSubtaskThinkSeg = { details: d, pre: pre, tail: tail };
      }
      _subtaskReasoningPendingText += t;
      scheduleSubtaskReasoningFlush();
      return;
    }
    const useBlock =
      typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "think";
    if (useBlock) {
      const cell = ensureThinkTimelineBlock(blockSeq);
      openThinkSeg = cell;
      cell.pre.textContent += t;
      cell.tail.textContent = formatActivityTail(cell.pre.textContent, TIMELINE_TAIL_MAX);
      cell.details.title = cell.pre.textContent;
      $messages.scrollTop = $messages.scrollHeight;
      return;
    }
    if (!openThinkSeg) openThinkSeg = newTimelineSeg("think", "推理");
    openThinkSeg.pre.textContent += t;
    openThinkSeg.tail.textContent = formatActivityTail(openThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
    openThinkSeg.details.title = openThinkSeg.pre.textContent;
    $messages.scrollTop = $messages.scrollHeight;
  }

  function beginStream() {
    const welcome = $messages.querySelector(".welcome");
    if (welcome) welcome.remove();

    finalizeOpenSubtaskToolDraft();
    cancelSubtaskReasoningRaf();
    _subtaskReasoningPendingText = "";
    _subtaskReasoningPendingSeq = null;
    openSubtaskThinkSeg = null;
    subtaskThinkCells = new Map();
    currentSubtaskReasoningRoot = null;

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

    $messages.appendChild(currentRoundWrapper);
    currentBotMessageRaw = "";
    currentRoundToolBlocks = [];
    currentToolEndIndex = 0;
    setInputEnabled(false);
  }

  function updateExecutionLogCount() {}

  function contextUsageRingCircumferenceOffice() {
    return 2 * Math.PI * 13;
  }

  function resetContextUsageRingOffice() {
    const wrap = document.getElementById("context-usage-ring-wrap");
    const prog = document.getElementById("context-usage-ring-progress");
    if (!wrap || !prog) return;
    const c = contextUsageRingCircumferenceOffice();
    wrap.hidden = true;
    wrap.removeAttribute("title");
    prog.style.strokeDasharray = String(c);
    prog.style.strokeDashoffset = String(c);
  }

  function applyStreamUsageToContextRingOffice(content) {
    const H = typeof openWorkmateHostShared !== "undefined" ? openWorkmateHostShared : null;
    const wrap = document.getElementById("context-usage-ring-wrap");
    const prog = document.getElementById("context-usage-ring-progress");
    if (!wrap || !prog || !H || !H.parseStreamUsagePayload || !H.usagePromptFillRatio) return;
    const c = contextUsageRingCircumferenceOffice();
    const parsed = H.parseStreamUsagePayload(content);
    if (!parsed) {
      wrap.hidden = true;
      prog.style.strokeDashoffset = String(c);
      return;
    }
    const fill = H.usagePromptFillRatio(parsed);
    if (fill == null) {
      wrap.hidden = true;
      prog.style.strokeDashoffset = String(c);
      return;
    }
    wrap.hidden = false;
    if (H.buildStreamUsageRingTitle) wrap.title = H.buildStreamUsageRingTitle(parsed);
    prog.style.strokeDasharray = String(c);
    prog.style.strokeDashoffset = String(c * (1 - Math.min(1, Math.max(0, fill))));
    $messages.scrollTop = $messages.scrollHeight;
  }

  /** stream_role / stream_meta：挂时间线诊断段（stream_usage / stream_finish 仅圆环或忽略，对齐 Chrome） */
  function appendOpenAiStreamMetaSeg(wsType, content, blockSeq, blockKind) {
    const body = (content != null && String(content).trim()) || "";
    if (!body) return;
    if (!currentRoundWrapper) beginStream();
    ensureTimeline();
    const titles = {
      stream_usage: "Token 用量",
      stream_finish: "结束原因",
      stream_role: "角色",
      stream_meta: "响应元数据"
    };
    const kinds = {
      stream_usage: "stream-usage",
      stream_finish: "stream-finish",
      stream_role: "stream-role",
      stream_meta: "stream-meta"
    };
    const title = titles[wsType] || "流事件";
    const kind = kinds[wsType] || "stream-meta";
    const d = document.createElement("details");
    d.className = "timeline-seg timeline-seg--" + kind;
    d.dataset.kind = kind;
    d.open = true;
    const sum = document.createElement("summary");
    const lab = document.createElement("span");
    lab.className = "timeline-seg__label";
    lab.textContent = title;
    const tail = document.createElement("span");
    tail.className = "timeline-seg__tail";
    sum.appendChild(lab);
    sum.appendChild(document.createTextNode(" "));
    sum.appendChild(tail);
    const pre = document.createElement("pre");
    pre.className = "timeline-seg__body";
    pre.textContent = body;
    d.appendChild(sum);
    d.appendChild(pre);
    tail.textContent = formatActivityTail(body.replace(/\s+/g, " ").trim(), TIMELINE_TAIL_MAX);
    d.title = body.slice(0, 500);
    const useOrder =
      typeof blockSeq === "number" &&
      Number.isFinite(blockSeq) &&
      (blockKind === "usage" || blockKind === "finish" || blockKind === "role" || blockKind === "meta");
    if (useOrder) insertTimelineBlockInOrder(d, blockSeq);
    else timelineRoot.appendChild(d);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function appendStreamWarning(text) {
    if (!currentRoundWrapper) beginStream();
    const wrap = currentRoundWrapper;
    if (!wrap) return;
    const notice = document.createElement("div");
    notice.className = "msg msg--stream-warning";
    notice.textContent = (text && String(text).trim()) || "服务端返回了警告";
    wrap.appendChild(notice);
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
  function appendStreamChunk(text, blockSeq, blockKind) {
    if (!currentRoundWrapper) beginStream();
    const chunk = text != null ? String(text) : "";
    const useBlock =
      typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "answer";

    if (useBlock) {
      if (!chunk) return;
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
      requestAnimationFrame(() => {
        _streamRenderPending = false;
        if (!openAnswerSeg) return;
        applyMarkedToElement(openAnswerSeg.body, openAnswerSeg.rawMd);
        const plain = openAnswerSeg.rawMd.replace(/\s+/g, " ").trim();
        openAnswerSeg.tail.textContent = formatActivityTail(plain, TIMELINE_TAIL_MAX);
        openAnswerSeg.details.title = plain.slice(0, 200);
        $messages.scrollTop = $messages.scrollHeight;
      });
      return;
    }

    if (!chunk) return;
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
    finalizeOpenToolDraftSeg();
    finalizeOpenSubtaskToolDraft();
    flushSubtaskReasoningPendingSync();
    cancelSubtaskReasoningRaf();
    openSubtaskThinkSeg = null;
    subtaskThinkCells = new Map();
    currentSubtaskReasoningRoot = null;
    clearAllRunningToolTimers();
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
      timelineRoot.querySelectorAll(":scope > details.timeline-seg").forEach(function (el) {
        el.open = false;
      });
      const ans = timelineRoot.querySelectorAll(".timeline-seg.timeline-seg--answer");
      if (ans.length) ans[ans.length - 1].open = true;
      timelineRoot.querySelectorAll(":scope > .tool-call-block").forEach(function (el) {
        el.open = false;
      });
    }
    currentBotMessageRaw = "";
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
    ensureBootstrapAuthToken().then(function (bootstrap) {
      var tok = getStoredAuthToken();
      var ap = getStoredAgentProfileId();
      var qs = "";
      if (typeof openWorkmateHostShared !== "undefined" && openWorkmateHostShared.buildWebSocketQueryString) {
        qs = openWorkmateHostShared.buildWebSocketQueryString({
          sessionId: sessionId,
          clientType: OFFICE_CLIENT_TYPE,
          agentProfileId: ap,
          token: tok,
          bootstrap: bootstrap,
          telemetryDeviceIdKey: openWorkmate_TELEMETRY_DEVICE_ID_KEY,
          telemetryClientEmissionKey: openWorkmate_TELEMETRY_CLIENT_EMISSION_KEY,
          telemetryRelayActiveProfileKey: openWorkmate_TELEMETRY_RELAY_ACTIVE_PROFILE_KEY,
          telemetryEventKindsByProfileKey: openWorkmate_TELEMETRY_EVENT_KINDS_BY_PROFILE_KEY
        });
      } else {
        qs =
          "?sessionId=" +
          encodeURIComponent(sessionId) +
          "&clientType=" +
          encodeURIComponent(OFFICE_CLIENT_TYPE) +
          "&agentProfileId=" +
          encodeURIComponent(ap) +
          (tok ? "&token=" + encodeURIComponent(tok) : "");
      }
      ws = new WebSocket(WS_URL + qs);

      ws.onopen = function () {
        reconnectDelay = RECONNECT_BASE_MS;
        setStatus(true);
        addSystemMessage("已连接到本地服务");
        sendOfficeSetContextIfNeeded();
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
      addSystemMessage("找不到本机 Open Workmate：" + (err && err.message ? err.message : String(err)));
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
    sendOfficeSetContextIfNeeded();
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
      await openWorkmateEnsureOfficeApiBase();
      const res = await openWorkmateFetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
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

  var historySkip = 0;
  var historyHasMore = true;
  var historyLoading = false;

  function showOfficeHistoryError(text) {
    if (!$historyError) return;
    var t = (text || "").trim();
    if (!t) {
      $historyError.style.display = "none";
      $historyError.textContent = "";
      return;
    }
    $historyError.textContent = t;
    $historyError.style.display = "block";
  }

  function updateOfficeHistoryLoadMoreButton() {
    if (!$historyLoadMore) return;
    if (!historyHasMore) {
      $historyLoadMore.textContent = "没有更多了";
      $historyLoadMore.disabled = true;
    } else {
      $historyLoadMore.textContent = "加载更多";
      $historyLoadMore.disabled = historyLoading;
    }
  }

  function closeOfficeHistoryOverlay() {
    if (!$historyOverlay) return;
    $historyOverlay.style.display = "none";
    $historyOverlay.setAttribute("aria-hidden", "true");
    showOfficeHistoryError("");
  }

  function appendOfficeHistoryListItem(it, currentSessionId) {
    if (!$historyList || !it || !it.sessionId) return;
    var li = document.createElement("li");
    li.className = "history-list-item" + (it.sessionId === currentSessionId ? " history-list-item--current" : "");
    li.dataset.sessionId = it.sessionId;
    if (it.agentProfileId != null && String(it.agentProfileId).trim() !== "")
      li.dataset.agentProfileId = String(it.agentProfileId).trim();
    var main = document.createElement("div");
    main.className = "history-list-item-main";
    var titleEl = document.createElement("div");
    titleEl.className = "history-list-item-title";
    titleEl.textContent = (it.titlePreview && String(it.titlePreview).trim()) || it.sessionId || "（无标题）";
    var meta = document.createElement("div");
    meta.className = "history-list-item-meta";
    if (it.updatedAtUtc) {
      try {
        meta.textContent = new Date(it.updatedAtUtc).toLocaleString();
      } catch (e) {
        meta.textContent = "";
      }
    }
    main.appendChild(titleEl);
    main.appendChild(meta);
    var delBtn = document.createElement("button");
    delBtn.type = "button";
    delBtn.className = "history-list-item-delete";
    delBtn.textContent = "删除";
    delBtn.title = "删除此历史对话";
    delBtn.addEventListener("click", function (ev) {
      ev.stopPropagation();
      void deleteOfficeHistorySession(it.sessionId, li);
    });
    li.addEventListener("click", function () {
      void switchToOfficeHistorySession(it.sessionId, it.agentProfileId);
    });
    li.appendChild(main);
    li.appendChild(delBtn);
    $historyList.appendChild(li);
  }

  async function fetchOfficeHistoryPage(append) {
    if (historyLoading) return;
    if (append && !historyHasMore) return;
    historyLoading = true;
    if ($historyLoadMore) $historyLoadMore.disabled = true;
    try {
      await openWorkmateEnsureOfficeApiBase();
      await ensureBootstrapAuthToken();
      var skip = append ? historySkip : 0;
      if (!append) {
        historySkip = 0;
        historyHasMore = true;
        if ($historyList) $historyList.innerHTML = "";
      }
      var ap = getStoredAgentProfileId();
      var res = await openWorkmateFetch(
        API_BASE + "/api/chat-sessions?skip=" + skip + "&take=10&agentProfileId=" + encodeURIComponent(ap)
      );
      var data = await res.json().catch(function () { return {}; });
      if (!res.ok) throw new Error(data.message || "加载历史列表失败");
      var items = data.items || [];
      historyHasMore = !!data.hasMore;
      historySkip = skip + items.length;
      var curSid = getSessionId();
      for (var i = 0; i < items.length; i++) appendOfficeHistoryListItem(items[i], curSid);
    } catch (e) {
      showOfficeHistoryError(e.message || String(e));
    } finally {
      historyLoading = false;
      updateOfficeHistoryLoadMoreButton();
    }
  }

  async function deleteOfficeHistorySession(sid, liEl) {
    if (!sid) return;
    if (!confirm("确定删除此历史对话？本地保存的记录将移除，且无法恢复。")) return;
    try {
      await openWorkmateEnsureOfficeApiBase();
      await ensureBootstrapAuthToken();
      var res = await openWorkmateFetch(API_BASE + "/api/chat-sessions/" + encodeURIComponent(sid), { method: "DELETE" });
      var data = await res.json().catch(function () { return {}; });
      if (!res.ok) {
        alert(data.message || "删除失败");
        return;
      }
      if (liEl && liEl.parentNode) liEl.parentNode.removeChild(liEl);
      if (getSessionId() === sid) {
        try {
          sessionStorage.removeItem("copilot_session_id");
        } catch (e) { /* ignore */ }
        lastSetContextSig = "";
        currentPlanId = null;
        currentPlanTitle = null;
        currentPlanContent = null;
        currentPlanCreatedBy = null;
        if ($planPanel) $planPanel.style.display = "none";
        planChecklistSteps = [];
        planChecklistStatus = {};
        planChecklistLoadedPlanId = null;
        if ($planChecklistWrap) $planChecklistWrap.style.display = "none";
        clearPlanCurrentStepIndex();
        if ($messages) $messages.innerHTML = OFFICE_WELCOME_INNER_HTML;
        attachments = [];
        renderAttachmentsPreview();
        if (ws) {
          try {
            ws.close();
          } catch (e2) { /* ignore */ }
          ws = null;
        }
        connect();
      }
    } catch (e) {
      alert(e.message || String(e));
    }
  }

  async function switchToOfficeHistorySession(sid, agentProfileIdFromItem) {
    if (!sid) return;
    finalizeStream();
    try {
      await openWorkmateEnsureOfficeApiBase();
      await ensureBootstrapAuthToken();
      if (agentProfileIdFromItem != null && String(agentProfileIdFromItem).trim() !== "") {
        persistAgentProfileId(String(agentProfileIdFromItem).trim());
        _suppressOfficeAgentSelectChange = true;
        try {
          if ($agentProfileSelect) {
            var ap = getStoredAgentProfileId();
            var ids = Array.prototype.map.call($agentProfileSelect.options, function (o) { return o.value; });
            if (ids.indexOf(ap) >= 0) $agentProfileSelect.value = ap;
          }
        } finally {
          _suppressOfficeAgentSelectChange = false;
        }
      }
      var res = await openWorkmateFetch(API_BASE + "/api/chat-sessions/" + encodeURIComponent(sid) + "/messages");
      var data = await res.json().catch(function () { return {}; });
      if (!res.ok) {
        addBotMessage(data.message || "加载该对话消息失败", true);
        return;
      }
      try {
        sessionStorage.setItem("copilot_session_id", sid);
      } catch (e) { /* ignore */ }
      sessionId = sid;
      lastSetContextSig = "";
      currentPlanId = null;
      currentPlanTitle = null;
      currentPlanContent = null;
      currentPlanCreatedBy = null;
      if ($planPanel) $planPanel.style.display = "none";
      planChecklistSteps = [];
      planChecklistStatus = {};
      planChecklistLoadedPlanId = null;
      if ($planChecklistWrap) $planChecklistWrap.style.display = "none";
      clearPlanCurrentStepIndex();
      attachments = [];
      renderAttachmentsPreview();
      if ($messages) $messages.innerHTML = "";
      var msgs = data.messages || [];
      for (var mi = 0; mi < msgs.length; mi++) {
        var m = msgs[mi];
        var r = (m.role || "").toLowerCase();
        if (r === "user") addUserMessage(m.text || "");
        else addBotMessage(m.text || "", false);
      }
      if (msgs.length === 0 && $messages) $messages.innerHTML = OFFICE_WELCOME_INNER_HTML;
      closeOfficeHistoryOverlay();
      if (ws) {
        try {
          ws.close();
        } catch (e2) { /* ignore */ }
        ws = null;
      }
      connect();
    } catch (e) {
      addBotMessage(e.message || String(e), true);
    }
  }

  async function openOfficeHistoryOverlay() {
    if (!$historyOverlay) return;
    showOfficeHistoryError("");
    historySkip = 0;
    historyHasMore = true;
    if ($historyList) $historyList.innerHTML = "";
    $historyOverlay.style.display = "flex";
    $historyOverlay.setAttribute("aria-hidden", "false");
    updateOfficeHistoryLoadMoreButton();
    try {
      await fetchOfficeHistoryPage(false);
    } catch (e) {
      showOfficeHistoryError(e.message || String(e));
    }
  }

  var officeAskOptionsOverlay = null;
  var $officeAskOptionsTitle = null;
  var $officeAskOptionsPrompt = null;
  var $officeAskOptionsStepIndicator = null;
  var $officeAskOptionsQuestion = null;
  var $officeAskOptionsOptions = null;
  var $officeAskOptionsConfirmBtn = null;
  var officeAskOptionsRequestId = null;
  var officeAskOptionsSteps = [];
  var officeAskOptionsSelections = {};
  var officeAskOptionsCurrentStepIndex = 0;
  var officeAskOptionsCurrentSelectedOptionId = null;
  var officeAskOptionsBound = false;
  var officeAskOptionsTitleCache = "";
  var officeAskOptionsPromptCache = "";

  function openOfficeAskOptionsOverlay() {
    if (!officeAskOptionsOverlay) return;
    officeAskOptionsOverlay.style.display = "flex";
    officeAskOptionsOverlay.setAttribute("aria-hidden", "false");
  }

  function closeOfficeAskOptionsOverlay() {
    if (!officeAskOptionsOverlay) return;
    officeAskOptionsOverlay.style.display = "none";
    officeAskOptionsOverlay.setAttribute("aria-hidden", "true");
    officeAskOptionsRequestId = null;
    officeAskOptionsSteps = [];
    officeAskOptionsSelections = {};
    officeAskOptionsCurrentStepIndex = 0;
    officeAskOptionsCurrentSelectedOptionId = null;
  }

  function setOfficeAskOptionsActiveOption(optionId) {
    officeAskOptionsCurrentSelectedOptionId = optionId || null;
    if (!$officeAskOptionsOptions) return;
    var items = $officeAskOptionsOptions.querySelectorAll(".ask-option-item");
    for (var i = 0; i < items.length; i++) {
      var el = items[i];
      var active = el.dataset.optionId === String(optionId || "");
      el.classList.toggle("ask-option-item--active", active);
    }
  }

  function renderOfficeAskOptionsStep(idx) {
    if (!$officeAskOptionsTitle || !$officeAskOptionsPrompt || !$officeAskOptionsStepIndicator || !$officeAskOptionsQuestion || !$officeAskOptionsOptions) return;
    if (!Array.isArray(officeAskOptionsSteps) || officeAskOptionsSteps.length === 0) return;
    var step = officeAskOptionsSteps[idx];
    if (!step) return;
    $officeAskOptionsTitle.textContent = String(step.title || "") || String(officeAskOptionsTitleCache || "请选择一个选项");
    $officeAskOptionsPrompt.textContent = String(officeAskOptionsPromptCache || "");
    $officeAskOptionsStepIndicator.textContent = "步骤 " + (idx + 1) + "/" + officeAskOptionsSteps.length;
    $officeAskOptionsQuestion.textContent = String(step.question || "");
    var selectedOptionId = officeAskOptionsSelections[step.stepId] || null;
    officeAskOptionsCurrentSelectedOptionId = selectedOptionId;
    var options = Array.isArray(step.options) ? step.options : [];
    var html = "";
    for (var oi = 0; oi < options.length; oi++) {
      var o = options[oi];
      var optionId = String(o.optionId != null ? o.optionId : "");
      var label = String(o.label != null ? o.label : "");
      var active = selectedOptionId && String(selectedOptionId) === optionId ? "ask-option-item--active" : "";
      html +=
        '<div class="ask-option-item ' +
        active +
        '" data-option-id="' +
        escapeHtml(optionId) +
        '" role="option" aria-selected="' +
        (active ? "true" : "false") +
        '"><div class="ask-option-label">' +
        escapeHtml(label || optionId) +
        "</div></div>";
    }
    $officeAskOptionsOptions.innerHTML = html;
  }

  function ensureOfficeAskOptionsBound() {
    if (officeAskOptionsBound) return;
    officeAskOptionsBound = true;
    if ($officeAskOptionsOptions) {
      $officeAskOptionsOptions.addEventListener("click", function (e) {
        var item = e.target && e.target.closest ? e.target.closest(".ask-option-item") : null;
        if (!item) return;
        setOfficeAskOptionsActiveOption(item.dataset.optionId);
      });
    }
    if ($officeAskOptionsConfirmBtn) {
      $officeAskOptionsConfirmBtn.addEventListener("click", function () {
        if (!officeAskOptionsRequestId) return;
        if (!Array.isArray(officeAskOptionsSteps) || officeAskOptionsSteps.length === 0) {
          sendOfficeAskOptionsResponse();
          return;
        }
        var step = officeAskOptionsSteps[officeAskOptionsCurrentStepIndex];
        if (!step) return;
        if (!officeAskOptionsCurrentSelectedOptionId) {
          alert("请先选择一个选项后再点击确定。");
          return;
        }
        officeAskOptionsSelections[step.stepId] = String(officeAskOptionsCurrentSelectedOptionId);
        if (officeAskOptionsCurrentStepIndex < officeAskOptionsSteps.length - 1) {
          officeAskOptionsCurrentStepIndex++;
          renderOfficeAskOptionsStep(officeAskOptionsCurrentStepIndex);
        } else {
          sendOfficeAskOptionsResponse();
        }
      });
    }
  }

  function sendOfficeAskOptionsResponse() {
    var id = officeAskOptionsRequestId;
    var selections = officeAskOptionsSelections || {};
    if (!id) return;
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage("连接已断开，无法提交候选项选择。", true);
      closeOfficeAskOptionsOverlay();
      setInputEnabled(true);
      return;
    }
    ws.send(JSON.stringify({ type: "ask_options_response", id: id, selections: selections }));
    closeOfficeAskOptionsOverlay();
    setInputEnabled(true);
    if ($stopBtn) $stopBtn.style.display = "none";
  }

  function handleOfficeAskOptionsRequest(msg) {
    try {
      if (!msg) return;
      var id = msg.id || msg.requestId;
      if (!id) return;
      var steps = Array.isArray(msg.steps) ? msg.steps : [];
      if (!steps.length) {
        officeAskOptionsRequestId = id;
        officeAskOptionsSteps = [];
        officeAskOptionsSelections = {};
        officeAskOptionsCurrentStepIndex = 0;
        officeAskOptionsCurrentSelectedOptionId = null;
        sendOfficeAskOptionsResponse();
        return;
      }
      officeAskOptionsOverlay = document.getElementById("ask-options-overlay");
      $officeAskOptionsTitle = document.getElementById("ask-options-title");
      $officeAskOptionsPrompt = document.getElementById("ask-options-prompt");
      $officeAskOptionsStepIndicator = document.getElementById("ask-options-step-indicator");
      $officeAskOptionsQuestion = document.getElementById("ask-options-question");
      $officeAskOptionsOptions = document.getElementById("ask-options-options");
      $officeAskOptionsConfirmBtn = document.getElementById("ask-options-confirm-btn");
      officeAskOptionsRequestId = id;
      officeAskOptionsSteps = steps;
      officeAskOptionsSelections = {};
      officeAskOptionsCurrentStepIndex = 0;
      officeAskOptionsCurrentSelectedOptionId = null;
      officeAskOptionsTitleCache = String(msg.title || "");
      officeAskOptionsPromptCache = String(msg.prompt || "");
      ensureOfficeAskOptionsBound();
      openOfficeAskOptionsOverlay();
      setInputEnabled(false);
      if ($stopBtn) $stopBtn.style.display = "none";
      renderOfficeAskOptionsStep(0);
    } catch (e) {
      console.error("ask_options_request UI error:", e);
      addBotMessage("弹出候选项选择失败，请重试。", true);
      closeOfficeAskOptionsOverlay();
      setInputEnabled(true);
    }
  }

  async function refreshOfficeAgentProfileSelect() {
    if (!$agentProfileSelect) return;
    try {
      await openWorkmateEnsureOfficeApiBase();
      await ensureBootstrapAuthToken();
      var res = await openWorkmateFetch(API_BASE + "/api/config");
      var data = await res.json().catch(function () { return {}; });
      if (!res.ok) return;
      var list = data.agentProfiles || data.AgentProfiles || [];
      _suppressOfficeAgentSelectChange = true;
      $agentProfileSelect.innerHTML = "";
      for (var i = 0; i < list.length; i++) {
        var p = list[i];
        var pid = String((p.id != null ? p.id : p.Id) || "").trim();
        if (!pid) continue;
        var opt = document.createElement("option");
        opt.value = pid;
        opt.textContent = p.displayName || p.DisplayName || pid;
        $agentProfileSelect.appendChild(opt);
      }
      if ($agentProfileSelect.options.length === 0) {
        var opt2 = document.createElement("option");
        opt2.value = "default";
        opt2.textContent = "默认助手";
        $agentProfileSelect.appendChild(opt2);
      }
      var serverDefault = String(data.activeAgentProfileId || data.ActiveAgentProfileId || "default").trim() || "default";
      var active = getStoredAgentProfileId() || serverDefault;
      var ids = Array.prototype.map.call($agentProfileSelect.options, function (o) { return o.value; });
      if (ids.indexOf(active) < 0) active = ids[0] || "default";
      persistAgentProfileId(active);
      $agentProfileSelect.value = active;
      var themeFromServer =
        (data.uiThemeId && String(data.uiThemeId).trim()) ||
        (data.UiThemeId && String(data.UiThemeId).trim()) ||
        "";
      if (themeFromServer && typeof OpenWorkmateTheme !== "undefined") {
        try {
          OpenWorkmateTheme.setTheme(themeFromServer);
          if (typeof window.openWorkmateOfficeRefreshEmbedThemes === "function") window.openWorkmateOfficeRefreshEmbedThemes();
        } catch (e) { /* ignore */ }
      }
    } catch (e) {
      console.warn("refreshOfficeAgentProfileSelect", e);
    } finally {
      _suppressOfficeAgentSelectChange = false;
    }
  }

  function applyOfficeAgentProfileChange(newId) {
    persistAgentProfileId(newId);
    try {
      sessionStorage.removeItem("copilot_session_id");
    } catch (e) { /* ignore */ }
    sessionId = getSessionId();
    lastSetContextSig = "";
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
    planChecklistLoadedPlanId = null;
    if ($planChecklistWrap) $planChecklistWrap.style.display = "none";
    clearPlanCurrentStepIndex();
    if ($messages) $messages.innerHTML = OFFICE_WELCOME_INNER_HTML;
    if ($input) $input.value = "";
    closeOfficeAskOptionsOverlay();
    closeOfficeHistoryOverlay();
    finalizeStream();
    if (ws) {
      try {
        ws.close();
      } catch (e) { /* ignore */ }
      ws = null;
    }
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    reconnectDelay = RECONNECT_BASE_MS;
    setInputEnabled(true);
    connect();
  }

  async function openOpenWorkmateSettingsInChrome() {
    try {
      await openWorkmateEnsureOfficeApiBase();
      await ensureBootstrapAuthToken();
      var res = await openWorkmateFetch(API_BASE + "/api/config");
      var j = {};
      if (res.ok) {
        try {
          j = await res.json();
        } catch (eParse) {
          j = {};
        }
      }
      var id = String(j.chromeExtensionId || j.ChromeExtensionId || "").trim();
      var url = id ? "chrome-extension://" + id + "/options.html" : "";
      if (url) window.open(url, "_blank");
      else addSystemMessage("未写入 Chrome 扩展 ID：请先在 Chrome 中打开本扩展「选项」页一次，或在后台 user-config 中配置 chromeExtensionId。");
    } catch (e) {
      addSystemMessage("无法打开设置：" + (e.message || String(e)));
    }
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
        if (tid && typeof OpenWorkmateTheme !== "undefined") OpenWorkmateTheme.setTheme(tid);
        if (typeof window.openWorkmateOfficeRefreshEmbedThemes === "function") window.openWorkmateOfficeRefreshEmbedThemes();
        break;
      }
      case "stream_start":
        resetContextUsageRingOffice();
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
        appendReasoningChunk(msg.content, msg.blockSeq, msg.blockKind, msg.isSubtask === true);
        break;
      case "tool_call_delta":
        appendToolCallDelta(msg);
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
        appendStreamChunk(msg.content, msg.blockSeq, msg.blockKind);
        break;
      case "stream_usage":
        applyStreamUsageToContextRingOffice(msg.content);
        break;
      case "stream_finish":
        break;
      case "stream_role":
        appendOpenAiStreamMetaSeg("stream_role", msg.content, msg.blockSeq, msg.blockKind);
        break;
      case "stream_meta":
        appendOpenAiStreamMetaSeg("stream_meta", msg.content, msg.blockSeq, msg.blockKind);
        break;
      case "stream_warning":
        appendStreamWarning(msg.content);
        break;
      case "stream_end":
        finalizeStream();
        break;
      case "subtask_start": {
        if (!currentRoundWrapper) beginStream();
        finalizeOpenSubtaskToolDraft();
        ensureTimeline();
        if (!timelineRoot) break;
        cancelSubtaskReasoningRaf();
        _subtaskReasoningPendingText = "";
        _subtaskReasoningPendingSeq = null;
        openSubtaskThinkSeg = null;
        subtaskThinkCells = new Map();
        const taskDesc = (msg.taskDescription && String(msg.taskDescription).trim()) || "子任务";
        const titleLen = 48;
        const summaryLabel = taskDesc.length <= titleLen ? taskDesc : taskDesc.slice(0, titleLen) + "…";
        const presetRaw = msg.subtaskPreset != null ? String(msg.subtaskPreset).trim() : "";
        const presetTag =
          presetRaw === "explore"
            ? "（探索）"
            : presetRaw === "cliShell"
              ? "（CLI）"
              : presetRaw === "browser"
                ? "（浏览器）"
                : "";
        const block = document.createElement("details");
        block.className = "subtask-block tool-call-block tool-call--running";
        block.dataset.label = "子代理" + presetTag + "：" + summaryLabel;
        block.dataset.subtaskPresetTag = presetTag;
        block.open = false;
        const sum = document.createElement("summary");
        sum.innerHTML =
          "<span class=\"tool-status-icon\">⏳</span> 子代理" + escapeHtml(presetTag) + "：" + escapeHtml(summaryLabel);
        block.appendChild(sum);
        const inner = document.createElement("div");
        inner.className = "subtask-inner";
        const thread = document.createElement("div");
        thread.className = "subtask-thread";
        const rail = document.createElement("div");
        rail.className = "subtask-thread__rail";
        rail.setAttribute("aria-hidden", "true");
        const threadBody = document.createElement("div");
        threadBody.className = "subtask-thread__body";
        const reasoningStack = document.createElement("div");
        reasoningStack.className = "subtask-reasoning-stack";
        threadBody.appendChild(reasoningStack);
        if (msg.taskDescription) {
          const taskEl = document.createElement("div");
          taskEl.className = "subtask-task";
          taskEl.textContent = "任务：" + String(msg.taskDescription).trim();
          threadBody.appendChild(taskEl);
        }
        if (msg.constraints && String(msg.constraints).trim()) {
          const conEl = document.createElement("div");
          conEl.className = "subtask-constraints";
          conEl.textContent = "约束：" + String(msg.constraints).trim();
          threadBody.appendChild(conEl);
        }
        const streamEl = document.createElement("pre");
        streamEl.className = "subtask-stream";
        threadBody.appendChild(streamEl);
        const toolsWrap = document.createElement("div");
        toolsWrap.className = "subtask-tools";
        threadBody.appendChild(toolsWrap);
        thread.appendChild(rail);
        thread.appendChild(threadBody);
        inner.appendChild(thread);
        block.appendChild(inner);
        timelineRoot.appendChild(block);
        currentSubtaskBlock = block;
        currentSubtaskReasoningRoot = reasoningStack;
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
        finalizeOpenSubtaskToolDraft();
        flushSubtaskReasoningPendingSync();
        cancelSubtaskReasoningRaf();
        openSubtaskThinkSeg = null;
        subtaskThinkCells = new Map();
        currentSubtaskReasoningRoot = null;
        if (currentSubtaskBlock) {
          const sum0 = currentSubtaskBlock.querySelector("summary");
          const doneTag = currentSubtaskBlock.dataset.subtaskPresetTag || "";
          if (sum0) {
            sum0.innerHTML =
              "<span class=\"tool-status-icon\">✓</span> 子代理" + escapeHtml(doneTag) + "（已完成）";
          }
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
        if (msg.isSubtask === true) finalizeOpenSubtaskToolDraft();
        else finalizeOpenToolDraftSeg();
        if (msg.planStepIndex) {
          if (msg.plugin === "Plan" && msg.function === "execute_plan_step") {
            const pid = currentPlanId;
            if (pid) {
              ensurePlanChecklistLoaded(pid).then(function () {
                updateChecklistStep(msg.planStepIndex, "in_progress");
              });
            } else {
              updateChecklistStep(msg.planStepIndex, "in_progress");
            }
          } else {
            updateChecklistStep(msg.planStepIndex, "in_progress");
          }
        }
        const label = msg.summary || "正在执行: " + (msg.plugin || "") + "." + (msg.function || "");
        const isSubtask = msg.isSubtask === true;
        collapsePhasesForToolStart();
        ensureTimeline();
        const parentBody = isSubtask ? currentSubtaskToolsEl : timelineRoot;
        if (!parentBody) break;
        const block = document.createElement("details");
        block.className = "tool-call-block tool-call--running" + (isSubtask ? " subtask-tool-block" : "");
        block.dataset.label = label;
        const invStart = msg.invocationId != null ? String(msg.invocationId).trim() : "";
        if (invStart) block.dataset.invocationId = invStart;
        const sum = document.createElement("summary");
        sum.innerHTML = "<span class=\"tool-status-icon\">⏳</span> " + escapeHtml(label);
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
        if (msg.planStepIndex) {
          if (msg.plugin === "Plan" && msg.function === "execute_plan_step") {
            updateChecklistStep(msg.planStepIndex, msg.success === true ? "done" : "pending");
            if (msg.success === true) {
              setPlanCurrentStepIndex(msg.planStepIndex + 1);
            }
          } else {
            updateChecklistStep(msg.planStepIndex, msg.success === true ? "done" : "pending");
          }
        }
        const isSubtask = msg.isSubtask === true;
        let block = null;
        const invEnd = msg.invocationId != null ? String(msg.invocationId).trim() : "";
        if (invEnd) {
          const scope = isSubtask ? currentSubtaskToolsEl : timelineRoot;
          if (scope && typeof scope.querySelector === "function") {
            var esc =
              typeof CSS !== "undefined" && CSS && typeof CSS.escape === "function"
                ? CSS.escape(invEnd)
                : invEnd.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
            block = scope.querySelector('[data-invocation-id="' + esc + '"]');
          }
        }
        if (!block) {
          block = isSubtask ? currentSubtaskToolBlocks[currentSubtaskToolEndIndex] : currentRoundToolBlocks[currentToolEndIndex];
        }
        if (block) {
          clearToolElapsedTimer(block);
          const contentRaw = (msg.content && String(msg.content).trim()) || "";
          const looksErr =
            typeof openWorkmateHostShared !== "undefined" &&
            openWorkmateHostShared.toolInvocationContentLooksLikeError
              ? openWorkmateHostShared.toolInvocationContentLooksLikeError(contentRaw)
              : false;
          const ok = msg.success === true && !looksErr;
          const name = (msg.plugin || "") + "." + (msg.function || "");
          const content = contentRaw;
          const displayLabel = (block.dataset.label || name).replace(/^正在执行:\s*/i, "");
          block.classList.remove("tool-call--running");
          block.classList.add(ok ? "tool-call--done" : "tool-call--fail");
          const sum = block.querySelector("summary");
          if (sum) sum.innerHTML = "<span class=\"tool-status-icon\">" + (ok ? "✓" : "✗") + "</span> " + escapeHtml(displayLabel);
          const out = block.querySelector(".tool-call-output");
          if (out) {
            out.textContent = decodeJsonStyleUnicodeEscapes(content || "");
            out.style.display = content ? "block" : "none";
          }
        }
        if (isSubtask) currentSubtaskToolEndIndex++;
        else currentToolEndIndex++;
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
      case "ask_options_request":
        handleOfficeAskOptionsRequest(msg);
        break;
      case "plan_created": {
        const planId = msg.planId || "";
        const title = msg.title || "新计划";
        const createdBy = (msg.createdBy || "").toLowerCase();
        if (planId && createdBy === OFFICE_CLIENT_TYPE) fetchPlanAndShow(planId, title, createdBy);
        break;
      }
      case "plan_updated": {
        const planId = msg.planId || "";
        const title = msg.title || planId || "计划";
        const createdBy = (msg.createdBy || "").toLowerCase();
        if (planId && createdBy === OFFICE_CLIENT_TYPE) {
          addSystemMessage("计划内容已更新，正在刷新任务窗格中的计划正文。");
          fetchPlanAndShow(planId, title, createdBy);
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
        await openWorkmateEnsureOfficeApiBase();
        const res = await openWorkmateFetch(API_BASE + "/api/plans/" + encodeURIComponent(currentPlanId), {
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

  /** @param {Record<string, unknown>} [p] */
  function docScriptParseMaxLength(p, defaultLen, cap) {
    var n = defaultLen;
    if (p && p.maxLength != null) {
      var x = parseInt(String(p.maxLength), 10);
      if (!isNaN(x) && x > 0) n = Math.min(x, cap);
    }
    return n;
  }
  const DOC_SCRIPT_MAX_BODY = 32000;

  // 预定义脚本注册表：仅执行已注册的 scriptId，供 run_document_script RPC 使用（Office.js，按宿主分支）
  const DOCUMENT_SCRIPTS = {
    word_read_selection: function (p) {
      if (OFFICE_CLIENT_TYPE === "office-word") {
        if (typeof Word === "undefined") return Promise.resolve("当前环境不是 Word，无法执行。");
        return Word.run(function (context) {
          const selection = context.document.getSelection();
          selection.load("text");
          return context.sync().then(function () {
            var t = selection.text || "";
            if (t.length > 8000) t = t.slice(0, 8000) + "\n...(已截断)";
            return t || "(无选区)";
          });
        });
      }
      if (OFFICE_CLIENT_TYPE === "office-excel") {
        if (typeof Excel === "undefined") return Promise.resolve("当前环境不是 Excel，无法执行。");
        return Excel.run(function (context) {
          var range = context.workbook.getSelectedRange();
          range.load("address", "text");
          return context.sync().then(function () {
            var raw = range.text;
            var s = "";
            if (Array.isArray(raw)) {
              s = raw.map(function (row) { return Array.isArray(row) ? row.join("\t") : String(row); }).join("\n");
            } else {
              s = raw != null ? String(raw) : "";
            }
            if (s.length > 8000) s = s.slice(0, 8000) + "\n...(已截断)";
            return "[Excel 选区 " + (range.address || "") + "]\n" + (s || "(空)");
          });
        });
      }
      if (OFFICE_CLIENT_TYPE === "office-powerpoint") {
        if (typeof PowerPoint === "undefined") return Promise.resolve("当前环境不是 PowerPoint，无法执行。");
        return PowerPoint.run(function (context) {
          var countResult = context.presentation.slides.getCount();
          return context.sync().then(function () {
            var total = countResult.value;
            if (total < 1) return "(无幻灯片)";
            var slides = context.presentation.getSelectedSlides();
            slides.load("items");
            return context.sync().then(function () {
              var slide =
                slides.items && slides.items.length > 0
                  ? slides.items[0]
                  : context.presentation.slides.getItemAt(0);
              slide.load("shapes/items/textFrame/textRange/text");
              return context.sync().then(function () {
                return docScriptPptSlideText_(slide, 1, total);
              });
            });
          });
        });
      }
      return Promise.resolve("未知的 Office 宿主，无法读取选区。OFFICE_CLIENT_TYPE=" + OFFICE_CLIENT_TYPE);
    },
    office_doc_meta: function (p) {
      var lines = [];
      lines.push("OFFICE_CLIENT_TYPE: " + OFFICE_CLIENT_TYPE);
      try {
        if (typeof Office !== "undefined" && Office.context && Office.context.document && Office.context.document.url) {
          lines.push("document.url: " + Office.context.document.url);
        } else {
          lines.push("document.url: （不可用或空）");
        }
      } catch (e) {
        lines.push("document.url: （读取失败）");
      }
      return Promise.resolve(lines.join("\n"));
    },
    office_word_body_preview: function (p) {
      if (OFFICE_CLIENT_TYPE !== "office-word") {
        return Promise.resolve("office_word_body_preview 仅适用于 Word。当前为 " + OFFICE_CLIENT_TYPE + "。");
      }
      if (typeof Word === "undefined") return Promise.resolve("Word API 不可用。");
      var maxLen = docScriptParseMaxLength(p, 2000, DOC_SCRIPT_MAX_BODY);
      return Word.run(function (context) {
        var range = context.document.body.getRange();
        range.load("text");
        return context.sync().then(function () {
          var t = range.text || "";
          var header = "[Word 正文摘录，最多 " + maxLen + " 字符]\n";
          if (t.length > maxLen) t = t.slice(0, maxLen) + "\n...(已截断)";
          return header + (t || "(无正文)");
        });
      });
    },
    office_host_quick_glance: function (p) {
      if (OFFICE_CLIENT_TYPE === "office-excel") {
        if (typeof Excel === "undefined") return Promise.resolve("Excel API 不可用。");
        return Excel.run(function (context) {
          var sheet = context.workbook.worksheets.getActiveWorksheet();
          sheet.load("name");
          var range = context.workbook.getSelectedRange();
          range.load("address", "values");
          return context.sync().then(function () {
            var vals = range.values;
            var preview = "";
            if (Array.isArray(vals)) {
              var rows = [];
              var maxR = Math.min(vals.length, 20);
              for (var r = 0; r < maxR; r++) {
                var row = vals[r];
                if (!Array.isArray(row)) continue;
                var maxC = Math.min(row.length, 16);
                var cells = [];
                for (var c = 0; c < maxC; c++) {
                  var v = row[c];
                  cells.push(v != null && v !== "" ? String(v) : "");
                }
                rows.push(cells.join("\t"));
              }
              preview = rows.join("\n");
            }
            if (preview.length > 6000) preview = preview.slice(0, 6000) + "\n...(已截断)";
            return "[Excel 快览]\n工作表: " + sheet.name + "\n区域: " + (range.address || "") + "\n---\n" + (preview || "(空)");
          });
        });
      }
      if (OFFICE_CLIENT_TYPE === "office-powerpoint") {
        if (typeof PowerPoint === "undefined") return Promise.resolve("PowerPoint API 不可用。");
        return PowerPoint.run(function (context) {
          var countResult = context.presentation.slides.getCount();
          return context.sync().then(function () {
            var total = countResult.value;
            if (total < 1) return "[PowerPoint 快览]\n(无幻灯片)";
            var slides = context.presentation.getSelectedSlides();
            slides.load("items");
            return context.sync().then(function () {
              var slide = null;
              var oneBased = 1;
              if (slides.items && slides.items.length > 0) {
                slide = slides.items[0];
              } else {
                slide = context.presentation.slides.getItemAt(0);
              }
              slide.load("shapes/items/textFrame/textRange/text");
              return context.sync().then(function () {
                return docScriptPptSlideText_(slide, oneBased, total);
              });
            });
          });
        });
      }
      return Promise.resolve("office_host_quick_glance 仅适用于 Excel 或 PowerPoint。当前为 " + OFFICE_CLIENT_TYPE + "。");
    }
  };

  function docScriptPptSlideText_(slide, indexOneBased, totalSlides) {
    var parts = [];
    if (slide.shapes && slide.shapes.items) {
      for (var i = 0; i < slide.shapes.items.length; i++) {
        var sh = slide.shapes.items[i];
        if (sh.textFrame && sh.textFrame.textRange && sh.textFrame.textRange.text) {
          parts.push(sh.textFrame.textRange.text);
        }
      }
    }
    var txt = parts.join(" ").trim() || "(无文本)";
    if (txt.length > 8000) txt = txt.slice(0, 8000) + "\n...(已截断)";
    return "[PowerPoint 第 " + indexOneBased + " / " + totalSlides + " 页]\n" + txt;
  }

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
          const styleRaw = params.style != null ? String(params.style).trim() : "";
          const styleNorm = styleRaw.toLowerCase().replace(/[\s_\-]+/g, "");
          await Word.run(function (context) {
            const body = context.document.body;
            const para = body.insertParagraph(text, "End");
            if (styleNorm) {
              var styleMap = {
                "heading1": Word.BuiltInStyleName.heading1,
                "heading2": Word.BuiltInStyleName.heading2,
                "heading3": Word.BuiltInStyleName.heading3,
                "normal": Word.BuiltInStyleName.normal,
                "title": Word.BuiltInStyleName.title,
                "subtitle": Word.BuiltInStyleName.subtitle
              };
              var builtIn = styleMap[styleNorm];
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

  // HITL：与 chrome-extension/sidepanel.js 同协议与同套 DOM 清理；规范源在 Chrome，勿以 WPS 为准。
  let pendingConfirmId = null;
  const $hitlOverlay = document.getElementById("hitl-overlay");
  const $hitlHumanSummary = document.getElementById("hitl-human-summary");
  const $hitlRawLabel = document.getElementById("hitl-raw-label");
  const $hitlAction = document.getElementById("hitl-action");
  const $hitlAllowBtn = document.getElementById("hitl-allow-btn");
  const $hitlAddToListBtn = document.getElementById("hitl-add-to-list-btn");
  const $hitlDenyBtn = document.getElementById("hitl-deny-btn");

  function handleConfirmRequest(msg) {
    const requestId = msg.id || msg.requestId;
    const action = msg.content || msg.action || "未知操作";
    const humanSummary = (msg.humanSummary && String(msg.humanSummary).trim()) || "";
    const hitlKind = msg.hitlKind;
    if (!requestId) return;
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
    if ($hitlAddToListBtn) $hitlAddToListBtn.style.display = (hitlKind === "run_command" || hitlKind === "page_agent") ? "" : "none";
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
    // 对齐 chrome-extension/sidepanel.js sendConfirmResponse
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
  if ($historyChatBtn) {
    $historyChatBtn.addEventListener("click", function () { void openOfficeHistoryOverlay(); });
  }
  if ($settingsBtn) {
    $settingsBtn.addEventListener("click", function () { void openOpenWorkmateSettingsInChrome(); });
  }
  if ($agentProfileSelect) {
    $agentProfileSelect.addEventListener("change", function () {
      if (_suppressOfficeAgentSelectChange) return;
      applyOfficeAgentProfileChange($agentProfileSelect.value);
    });
  }
  if ($historyOverlayClose) {
    $historyOverlayClose.addEventListener("click", closeOfficeHistoryOverlay);
  }
  if ($historyOverlayBackdrop) {
    $historyOverlayBackdrop.addEventListener("click", closeOfficeHistoryOverlay);
  }
  if ($historyLoadMore) {
    $historyLoadMore.addEventListener("click", function () { void fetchOfficeHistoryPage(true); });
  }
  document.addEventListener("keydown", function (ev) {
    if (ev.key !== "Escape") return;
    if ($historyOverlay && $historyOverlay.style.display !== "none" && $historyOverlay.getAttribute("aria-hidden") === "false") {
      closeOfficeHistoryOverlay();
    }
    if (officeAskOptionsOverlay && officeAskOptionsOverlay.style.display !== "none") {
      closeOfficeAskOptionsOverlay();
      setInputEnabled(true);
    }
  });
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
    void refreshOfficeAgentProfileSelect();
    connect();
  }

  if (typeof Office !== "undefined") {
    Office.onReady(function () { boot(); });
  } else {
    boot();
  }
})();
