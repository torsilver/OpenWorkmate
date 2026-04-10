(function () {
  "use strict";

  var WS_URL = "ws://127.0.0.1:8765/ws";
  var API_BASE = "http://127.0.0.1:8765";
  var tasklyWpsApiReady = null;
  function tasklyEnsureWpsApiBase() {
    if (tasklyWpsApiReady) return tasklyWpsApiReady;
    tasklyWpsApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(null).then(function (r) {
      var hw = TasklyLocalService.tasklyHttpWsFromBase(r.baseUrl);
      API_BASE = hw.apiBase;
      WS_URL = hw.wsUrl;
    });
    return tasklyWpsApiReady;
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
    return tasklyEnsureWpsApiBase().then(function () {
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
  const CLIENT_TYPE = "wps";

  (function tasklySyncThemeFromBackend() {
    try {
      tasklyEnsureWpsApiBase().then(function () {
      tasklyFetch(API_BASE + "/api/config")
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (j) {
          if (!j || typeof TasklyTheme === "undefined") return;
          var id = j.uiThemeId || j.UiThemeId;
          if (id) TasklyTheme.setTheme(id);
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

  var currentPlanId = null;
  var currentPlanTitle = null;
  var currentPlanContent = null;
  var currentPlanCreatedBy = null;

  let ws = null;
  let sessionId = null;
  let reconnectDelay = RECONNECT_BASE_MS;
  let reconnectTimer = null;
  let pendingMessages = [];
  let crossAgentAutoRunLock = false;
  let crossAgentAutoRunQueued = false;
  var CROSS_AGENT_AUTO_TRIGGER_TEXT =
    "请根据系统说明中「来自其他端的待办」逐项执行；每完成一项请调用 complete_cross_agent_task 标记完成。除待办外请勿延伸闲聊。";

  function getSessionId() {
    var id = sessionStorage.getItem("copilot_session_id");
    if (!id) {
      id = crypto.randomUUID().replace(/-/g, "").slice(0, 12);
      sessionStorage.setItem("copilot_session_id", id);
    }
    return id;
  }

  function setStatus(connected) {
    if (!$status) return;
    $status.className = connected ? "status status--connected" : "status status--disconnected";
    var t = $status.querySelector(".status-text");
    if (t) t.textContent = connected ? "已连接" : "未连接";
  }

  function addSystemMessage(text) {
    var w = $messages.querySelector(".welcome");
    if (w) w.remove();
    var div = document.createElement("div");
    div.className = "msg msg--system";
    div.textContent = text;
    $messages.appendChild(div);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function addUserMessage(text) {
    var w = $messages.querySelector(".welcome");
    if (w) w.remove();
    var div = document.createElement("div");
    div.className = "msg msg--user";
    div.textContent = text || "";
    $messages.appendChild(div);
    $messages.scrollTop = $messages.scrollHeight;
  }

  function addBotMessage(text, isError) {
    var w = $messages.querySelector(".welcome");
    if (w) w.remove();
    var div = document.createElement("div");
    div.className = "msg msg--bot" + (isError ? " msg--error" : "");
    if (typeof marked !== "undefined") {
      div.innerHTML = marked.parse((isError && text ? "⚠️ " : "") + (text || ""));
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

  function wpsPptCollectionCount(collection) {
    if (!collection) return 0;
    var count = collection.Count;
    if (typeof count !== "number") count = count != null && count.value !== undefined ? count.value : 0;
    return count;
  }

  function parsePptReorder1Based(newOrderStr, n) {
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
    for (var pi = 0; pi < order.length; pi++) {
      if (isNaN(order[pi])) return { err: "[错误] newOrder 中含无法解析的序号。" };
    }
    if (order.length !== n) return { err: "[错误] newOrder 长度须等于当前幻灯片张数（" + n + "）。" };
    var seen = {};
    for (var pj = 0; pj < order.length; pj++) {
      var xv = order[pj];
      if (xv < 1 || xv > n) return { err: "[错误] newOrder 中序号须在 1～" + n + " 之间。" };
      if (seen[xv]) return { err: "[错误] newOrder 中序号须无重复。" };
      seen[xv] = true;
    }
    return { order: order };
  }

  function parsePptRowsCsv(rowsCsv) {
    if (!rowsCsv || !String(rowsCsv).trim()) return { err: "[错误] 请提供 rowsCsv。" };
    var rows = String(rowsCsv)
      .split("|")
      .map(function (x) {
        return x.trim();
      })
      .filter(function (x) {
        return x.length > 0;
      });
    return {
      rows: rows.map(function (line) {
        return line.split(",").map(function (cell) {
          return String(cell !== undefined ? cell : "").trim();
        });
      }),
    };
  }

  function wpsPptNotesPlainText(notesPage) {
    if (!notesPage || !notesPage.Shapes) return "";
    var parts = [];
    var n = wpsPptCollectionCount(notesPage.Shapes);
    for (var i = 1; i <= n; i++) {
      var sh = notesPage.Shapes.Item(i);
      if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
        parts.push(String(sh.TextFrame.TextRange.Text));
      }
    }
    return parts.join("\n").trim();
  }

  function wpsPptSetNotesPlainText(notesPage, text) {
    if (!notesPage || !notesPage.Shapes) return;
    var n = wpsPptCollectionCount(notesPage.Shapes);
    for (var j = 1; j <= n; j++) {
      var sh2 = notesPage.Shapes.Item(j);
      if (sh2 && sh2.TextFrame && sh2.TextFrame.TextRange) {
        sh2.TextFrame.TextRange.Text = text;
        return;
      }
    }
    if (n > 0) {
      var sh3 = notesPage.Shapes.Item(1);
      if (sh3 && sh3.TextFrame && sh3.TextFrame.TextRange) sh3.TextFrame.TextRange.Text = text;
    }
  }

  function wpsPptFindFirstTableShape(slide) {
    if (!slide || !slide.Shapes) return null;
    var nt = wpsPptCollectionCount(slide.Shapes);
    for (var k = 1; k <= nt; k++) {
      var sh4 = slide.Shapes.Item(k);
      if (!sh4) continue;
      if (sh4.HasTable) return sh4;
      try {
        if (sh4.Table) return sh4;
      } catch (e0) {}
    }
    return null;
  }

  function wpsPptPickTextShape(slide, shapeIndex, shapeName) {
    if (!slide || !slide.Shapes) return null;
    var nt2 = wpsPptCollectionCount(slide.Shapes);
    if (shapeIndex > 0 && shapeIndex <= nt2) {
      var cand = slide.Shapes.Item(shapeIndex);
      if (cand && cand.TextFrame && cand.TextFrame.TextRange) return cand;
    }
    if (shapeName) {
      for (var si = 0; si < nt2; si++) {
        var it = slide.Shapes.Item(si + 1);
        if (it && it.Name && String(it.Name).toLowerCase() === shapeName.toLowerCase() && it.TextFrame && it.TextFrame.TextRange) {
          return it;
        }
      }
    }
    return null;
  }

  var currentBotMessageRaw = "";
  var currentRoundWrapper = null;
  var timelineRoot = null;
  var openPrepSeg = null;
  var openThinkSeg = null;
  var openDigestSeg = null;
  var openIntentSeg = null;
  var openAnswerSeg = null;
  var TIMELINE_TAIL_MAX = 100;
  var currentRoundToolBlocks = [];
  var currentToolEndIndex = 0;
  var timelineThinkCells = new Map();
  var timelineAnswerCells = new Map();

  function insertTimelineBlockInOrder(detailsEl, blockSeq) {
    ensureTimeline();
    detailsEl.dataset.blockSeq = String(blockSeq);
    var nodes = timelineRoot.children;
    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      var raw = node.dataset && node.dataset.blockSeq;
      if (raw == null || raw === "") continue;
      var n = parseInt(raw, 10);
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
      var raw = el.dataset.blockSeq;
      if (raw == null || raw === "") return;
      var s = parseInt(raw, 10);
      if (Number.isFinite(s) && s < answerSeq) el.open = false;
    });
  }

  function ensureThinkTimelineBlock(blockSeq) {
    var cell = timelineThinkCells.get(blockSeq);
    if (cell) return cell;
    ensureTimeline();
    var d = document.createElement("details");
    d.className = "timeline-seg timeline-seg--think";
    d.dataset.kind = "think";
    d.open = true;
    var sum = document.createElement("summary");
    var lab = document.createElement("span");
    lab.className = "timeline-seg__label";
    lab.textContent = "推理";
    var tail = document.createElement("span");
    tail.className = "timeline-seg__tail";
    sum.appendChild(lab);
    sum.appendChild(document.createTextNode(" "));
    sum.appendChild(tail);
    var pre = document.createElement("pre");
    pre.className = "timeline-seg__body";
    d.appendChild(sum);
    d.appendChild(pre);
    insertTimelineBlockInOrder(d, blockSeq);
    cell = { details: d, pre: pre, tail: tail };
    timelineThinkCells.set(blockSeq, cell);
    return cell;
  }

  function ensureAnswerTimelineBlock(blockSeq) {
    var cell = timelineAnswerCells.get(blockSeq);
    if (cell) return cell;
    ensureTimeline();
    var d = document.createElement("details");
    d.className = "timeline-seg timeline-seg--answer";
    d.dataset.kind = "answer";
    d.open = true;
    var sum = document.createElement("summary");
    var lab = document.createElement("span");
    lab.className = "timeline-seg__label";
    lab.textContent = "助手回复";
    var tail = document.createElement("span");
    tail.className = "timeline-seg__tail";
    sum.appendChild(lab);
    sum.appendChild(document.createTextNode(" "));
    sum.appendChild(tail);
    var body = document.createElement("div");
    body.className = "timeline-seg__body timeline-seg__body--md";
    d.appendChild(sum);
    d.appendChild(body);
    insertTimelineBlockInOrder(d, blockSeq);
    cell = { details: d, body: body, tail: tail, rawMd: "" };
    timelineAnswerCells.set(blockSeq, cell);
    return cell;
  }

  function formatActivityTail(log, maxChars) {
    var s = log || "";
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
    var d = document.createElement("details");
    d.className = "timeline-seg timeline-seg--answer";
    d.dataset.kind = "answer";
    d.open = true;
    var sum = document.createElement("summary");
    var lab = document.createElement("span");
    lab.className = "timeline-seg__label";
    lab.textContent = "助手回复";
    var tail = document.createElement("span");
    tail.className = "timeline-seg__tail";
    sum.appendChild(lab);
    sum.appendChild(document.createTextNode(" "));
    sum.appendChild(tail);
    var body = document.createElement("div");
    body.className = "timeline-seg__body timeline-seg__body--md";
    d.appendChild(sum);
    d.appendChild(body);
    timelineRoot.appendChild(d);
    return { details: d, body: body, tail: tail, rawMd: "" };
  }

  function runMermaidInTimeline(root) {
    if (!root || typeof mermaid === "undefined") return;
    root.querySelectorAll(".timeline-seg--answer .language-mermaid").forEach(function (block, index) {
      var id = "mermaid-" + Date.now() + "-" + index;
      var code = block.textContent;
      var container = document.createElement("div");
      container.className = "mermaid-container";
      container.id = id;
      block.parentNode.replaceWith(container);
      mermaid.render(id + "-svg", code).then(function (result) {
        container.innerHTML = result.svg;
      }).catch(function (err) {
        container.innerHTML = "<pre>Mermaid Error: " + err.message + "</pre>";
      });
    });
  }

  function newTimelineSeg(kind, titleLabel) {
    ensureTimeline();
    var d = document.createElement("details");
    d.className = "timeline-seg timeline-seg--" + kind;
    d.dataset.kind = kind;
    d.open = true;
    var sum = document.createElement("summary");
    var lab = document.createElement("span");
    lab.className = "timeline-seg__label";
    lab.textContent = titleLabel;
    var tail = document.createElement("span");
    tail.className = "timeline-seg__tail";
    sum.appendChild(lab);
    sum.appendChild(document.createTextNode(" "));
    sum.appendChild(tail);
    var pre = document.createElement("pre");
    pre.className = "timeline-seg__body";
    d.appendChild(sum);
    d.appendChild(pre);
    timelineRoot.appendChild(d);
    return { details: d, pre: pre, tail: tail };
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
    var line = (text && String(text).trim()) || "";
    if (!line) return;
    if (!currentRoundWrapper) beginStream();
    if (!openPrepSeg) openPrepSeg = newTimelineSeg("prep", "准备 / 状态");
    var pre = openPrepSeg.pre;
    if (pre.textContent) pre.textContent += "\n";
    pre.textContent += line;
    openPrepSeg.tail.textContent = formatActivityTail(pre.textContent, TIMELINE_TAIL_MAX);
    openPrepSeg.details.title = pre.textContent;
    if ($messages) $messages.scrollTop = $messages.scrollHeight;
  }

  function appendAgentTrace(msg) {
    var title = ((msg.traceTitle && String(msg.traceTitle).trim()) || (msg.content && String(msg.content).trim()) || "");
    var detail = (msg.traceDetail && String(msg.traceDetail).trim()) || "";
    var cat = (msg.traceCategory && String(msg.traceCategory).trim()) || "trace";
    if (!title && !detail) return;
    var block = "[" + cat + "] " + (title || "(无标题)");
    if (detail) block += "\n" + detail;
    appendAgentStatusLine(block);
  }

  function appendReasoningChunk(text, blockSeq, blockKind) {
    var t = text != null ? String(text) : "";
    if (!t) return;
    if (!currentRoundWrapper) beginStream();
    var useBlock =
      typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "think";
    if (useBlock) {
      var cell = ensureThinkTimelineBlock(blockSeq);
      openThinkSeg = cell;
      cell.pre.textContent += t;
      cell.tail.textContent = formatActivityTail(cell.pre.textContent, TIMELINE_TAIL_MAX);
      cell.details.title = cell.pre.textContent;
      if ($messages) $messages.scrollTop = $messages.scrollHeight;
      return;
    }
    if (!openThinkSeg) openThinkSeg = newTimelineSeg("think", "推理");
    openThinkSeg.pre.textContent += t;
    openThinkSeg.tail.textContent = formatActivityTail(openThinkSeg.pre.textContent, TIMELINE_TAIL_MAX);
    openThinkSeg.details.title = openThinkSeg.pre.textContent;
    if ($messages) $messages.scrollTop = $messages.scrollHeight;
  }

  function beginStream() {
    var w = $messages.querySelector(".welcome");
    if (w) w.remove();
    currentRoundWrapper = document.createElement("div");
    currentRoundWrapper.className = "msg msg--round";
    timelineRoot = null;
    openPrepSeg = null;
    openThinkSeg = null;
    openDigestSeg = null;
    openIntentSeg = null;
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

  function appendStreamWarning(text) {
    if (!currentRoundWrapper) beginStream();
    var wrap = currentRoundWrapper;
    if (!wrap) return;
    var notice = document.createElement("div");
    notice.className = "msg msg--stream-warning";
    notice.textContent = (text && String(text).trim()) || "服务端返回了警告";
    wrap.appendChild(notice);
    if ($messages) $messages.scrollTop = $messages.scrollHeight;
  }

  function applyMarkedToElement(el, rawMarkdown) {
    if (!el) return;
    var raw = rawMarkdown != null ? String(rawMarkdown) : "";
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

  var _streamRenderPending = false;
  function appendStreamChunk(text, blockSeq, blockKind) {
    if (!currentRoundWrapper) beginStream();
    var chunk = text != null ? String(text) : "";
    var useBlock =
      typeof blockSeq === "number" && Number.isFinite(blockSeq) && blockKind === "answer";

    if (useBlock) {
      if (!chunk) return;
      collapseThinkSegmentsWithSeqLessThan(blockSeq);
      openThinkSeg = null;
      collapseSeg(openDigestSeg);
      openDigestSeg = null;
      currentBotMessageRaw += chunk;
      var cell = ensureAnswerTimelineBlock(blockSeq);
      openAnswerSeg = cell;
      cell.rawMd += chunk;
      cell.details.dataset.streamRaw = cell.rawMd;
      if (_streamRenderPending) return;
      _streamRenderPending = true;
      requestAnimationFrame(function () {
        _streamRenderPending = false;
        if (!openAnswerSeg) return;
        applyMarkedToElement(openAnswerSeg.body, openAnswerSeg.rawMd);
        var plain = openAnswerSeg.rawMd.replace(/\s+/g, " ").trim();
        openAnswerSeg.tail.textContent = formatActivityTail(plain, TIMELINE_TAIL_MAX);
        openAnswerSeg.details.title = plain.slice(0, 200);
        if ($messages) $messages.scrollTop = $messages.scrollHeight;
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
    requestAnimationFrame(function () {
      _streamRenderPending = false;
      if (!openAnswerSeg) return;
      applyMarkedToElement(openAnswerSeg.body, openAnswerSeg.rawMd);
      var plain = openAnswerSeg.rawMd.replace(/\s+/g, " ").trim();
      openAnswerSeg.tail.textContent = formatActivityTail(plain, TIMELINE_TAIL_MAX);
      openAnswerSeg.details.title = plain.slice(0, 200);
      if ($messages) $messages.scrollTop = $messages.scrollHeight;
    });
  }

  function finalizeStream() {
    collapseAllOpenPhases();
    openAnswerSeg = null;
    if (timelineRoot) {
      timelineRoot.querySelectorAll(".timeline-seg--answer").forEach(function (el) {
        var div = el.querySelector(".timeline-seg__body--md");
        var raw = el.dataset.streamRaw;
        if (div && raw != null && typeof marked !== "undefined") {
          applyMarkedToElement(div, raw);
        }
      });
      if (typeof mermaid !== "undefined") runMermaidInTimeline(timelineRoot);
      var allD = timelineRoot.querySelectorAll("details");
      for (var di = 0; di < allD.length; di++) allD[di].open = false;
      var ans = timelineRoot.querySelectorAll(".timeline-seg.timeline-seg--answer");
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
    var url = WS_URL + "?sessionId=" + encodeURIComponent(sessionId) + "&clientType=" + encodeURIComponent(CLIENT_TYPE) + (tok ? "&token=" + encodeURIComponent(tok) : "");
    ws = new WebSocket(url);
    ws.onopen = function () {
      reconnectDelay = RECONNECT_BASE_MS;
      setStatus(true);
      addSystemMessage("已连接到本地服务");
      while (pendingMessages.length > 0) {
        var m = pendingMessages.shift();
        send(m.text);
      }
      flushCrossAgentAutoRunAfterReconnect();
    };
    ws.onmessage = function (e) { handleMessage(e.data); };
    ws.onclose = function () {
      var wasStreaming = !!currentRoundWrapper;
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
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage("连接已断开，消息未发送。请检查后台是否启动，并在 Chrome 扩展中配置。", true);
      return;
    }
    var payload = { type: "text", content: text || "" };
    var skipPlan = sendOpts && sendOpts.skipPlan === true;
    if (!skipPlan && currentPlanId) {
      payload.mode = "agent";
      payload.planId = currentPlanId;
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
    send(CROSS_AGENT_AUTO_TRIGGER_TEXT, { skipPlan: true });
  }

  function onCrossAgentTaskPush(msg) {
    var tid = msg && msg.taskId != null ? String(msg.taskId) : "";
    var desc = msg && msg.description != null ? String(msg.description).trim() : "";
    var line = "已收到来自其他端的跨端任务";
    if (tid) line += "（id=" + tid + "）";
    line += "。";
    if (desc) {
      var max = 180;
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

  function fetchPlanAndShow(planId, title, createdBy) {
    if (createdBy !== CLIENT_TYPE) return;
    tasklyEnsureWpsApiBase().then(function () {
    return tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(planId))
      .then(function (res) {
        return res.json().catch(function () { return {}; }).then(function (data) {
          if (!res.ok) return Promise.reject(new Error((data && data.message) || ("请求失败 " + res.status)));
          return data;
        });
      })
      .then(function (data) {
        currentPlanId = planId;
        currentPlanTitle = title || (data.meta && data.meta.title) || planId;
        currentPlanContent = data.content || "";
        currentPlanCreatedBy = (data.meta && data.meta.createdBy) || createdBy;
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
      })
      .catch(function (e) {
        console.error("fetch plan failed", e);
        alert(e.message || "加载计划失败");
      });
    });
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
    var msg;
    try { msg = JSON.parse(raw); } catch (e) { msg = { type: "text", content: raw }; }
    switch (msg.type) {
      case "ui_theme_changed": {
        var tid = (msg.uiThemeId && String(msg.uiThemeId).trim()) || "";
        if (tid && typeof TasklyTheme !== "undefined") TasklyTheme.setTheme(tid);
        break;
      }
      case "stream_start": beginStream(); break;
      case "agent_status": {
        var line = (msg.content && String(msg.content).trim()) || "";
        if (line) appendAgentStatusLine(line);
        break;
      }
      case "agent_trace":
        appendAgentTrace(msg);
        break;
      case "reasoning_chunk": appendReasoningChunk(msg.content, msg.blockSeq, msg.blockKind); break;
      case "agent_phase": {
        var phase = (msg.phase && String(msg.phase)) || "";
        var c = (msg.content && String(msg.content).trim()) || "";
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
        if ($messages) $messages.scrollTop = $messages.scrollHeight;
        break;
      }
      case "stream_chunk": appendStreamChunk(msg.content, msg.blockSeq, msg.blockKind); break;
      case "stream_warning": appendStreamWarning(msg.content); break;
      case "stream_end": finalizeStream(); break;
      case "subtask_start": {
        if (!currentRoundWrapper) beginStream();
        var taskDesc = (msg.taskDescription && String(msg.taskDescription).trim()) || "子任务";
        var titleLen = 48;
        var summaryLabel = taskDesc.length <= titleLen ? taskDesc : taskDesc.slice(0, titleLen) + "…";
        appendAgentStatusLine("子代理：" + summaryLabel);
        break;
      }
      case "subtask_chunk":
      case "subtask_end":
        break;
      case "tool_invocation_start":
        collapsePhasesForToolStart();
        ensureTimeline();
        if (!timelineRoot) break;
        var label = msg.summary || "正在执行: " + (msg.plugin || "") + "." + (msg.function || "");
        var block = document.createElement("details");
        block.className = "tool-call-block tool-call--running";
        block.dataset.label = label;
        var sum = document.createElement("summary");
        sum.innerHTML = "<span class=\"tool-status-icon\">⏳</span> " + escapeHtml(label);
        block.appendChild(sum);
        var out = document.createElement("pre");
        out.className = "tool-call-output";
        block.appendChild(out);
        timelineRoot.appendChild(block);
        currentRoundToolBlocks.push(block);
        block.open = true;
        updateExecutionLogCount();
        break;
      case "tool_invocation_end": {
        var block = currentRoundToolBlocks[currentToolEndIndex];
        if (block) {
          var ok = msg.success === true;
          var name = (msg.plugin || "") + "." + (msg.function || "");
          var content = (msg.content && String(msg.content).trim()) || "";
          var displayLabel = (block.dataset.label || name).replace(/^正在执行:\s*/i, "");
          block.classList.remove("tool-call--running");
          block.classList.add(ok ? "tool-call--done" : "tool-call--fail");
          var sumEl = block.querySelector("summary");
          if (sumEl) sumEl.innerHTML = "<span class=\"tool-status-icon\">" + (ok ? "✓" : "✗") + "</span> " + escapeHtml(displayLabel);
          var outEl = block.querySelector(".tool-call-output");
          if (outEl) { outEl.textContent = content || ""; outEl.style.display = content ? "block" : "none"; }
        }
        currentToolEndIndex++;
        break;
      }
      case "echo":
      case "text": addBotMessage(msg.content); break;
      case "pong": break;
      case "error":
        finalizeStream();
        addBotMessage((msg.content && String(msg.content).trim()) || "请求失败，请稍后重试。", true);
        break;
      case "rpc_request": handleRpcRequest(msg); break;
      case "confirm_request": handleConfirmRequest(msg); break;
      case "plan_created": {
        var planId = msg.planId || "";
        var title = msg.title || "新计划";
        var createdBy = (msg.createdBy || "").toLowerCase();
        if (planId && createdBy === CLIENT_TYPE) fetchPlanAndShow(planId, title, createdBy);
        break;
      }
      case "plan_updated": {
        var planIdU = msg.planId || "";
        var titleU = msg.title || planIdU || "计划";
        var createdByU = (msg.createdBy || "").toLowerCase();
        if (planIdU && createdByU === CLIENT_TYPE) {
          addSystemMessage("计划内容已更新，正在刷新任务窗格中的计划正文。");
          fetchPlanAndShow(planIdU, titleU, createdByU);
        }
        break;
      }
      case "cross_agent_task":
        onCrossAgentTaskPush(msg);
        break;
      case "cross_agent_task_completed": {
        var st = (msg.status && String(msg.status)) || "";
        var rs = (msg.resultSummary && String(msg.resultSummary).trim()) || "";
        var tid2 = msg.taskId != null ? String(msg.taskId) : "";
        var line2 = "跨端任务已由对方处理" + (tid2 ? "（id=" + tid2 + "）" : "");
        if (st) line2 += "，状态：" + st;
        line2 += rs ? "。" + (rs.length > 160 ? rs.slice(0, 160) + "…" : rs) : "。";
        addSystemMessage(line2);
        break;
      }
      default: addBotMessage(msg.content || JSON.stringify(msg));
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
    $planSaveBtn.addEventListener("click", function () {
      if (!currentPlanId) return;
      var content = $planContentEdit.value;
      tasklyEnsureWpsApiBase().then(function () {
      return tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(currentPlanId), {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ content: content })
      }).then(async function (res) {
        var data = await res.json().catch(function () { return {}; });
        if (!res.ok) throw new Error((data && data.message) || "保存失败");
        return data;
      }).then(function () {
        currentPlanContent = content;
        if ($planContentView) {
          $planContentView.innerHTML = (typeof marked !== "undefined") ? marked.parse(content || "") : escapeHtml(content || "");
        }
        cancelPlanEdit();
      }).catch(function (e) {
        console.error("save plan failed", e);
        alert(e.message || "保存失败");
      });
      });
    });
  }

  function wpsDocScriptMaxLen(p, def, cap) {
    var n = def;
    if (p && p.maxLength != null) {
      var x = parseInt(String(p.maxLength), 10);
      if (!isNaN(x) && x > 0) n = Math.min(x, cap);
    }
    return n;
  }

  var DOCUMENT_SCRIPTS = {
    word_read_selection: function (p) {
      if (!window.wps || !window.wps.ActiveDocument) return Promise.resolve("当前环境不可用。");
      try {
        var sel = window.wps.Selection;
        var t = (sel && sel.Text) ? sel.Text : "";
        t = t || "";
        if (t.length > 8000) t = t.slice(0, 8000) + "\n...(已截断)";
        return Promise.resolve(t || "(无选区)");
      } catch (e) {
        return Promise.resolve("WPS 暂不支持读取选区，请使用 word_read_body。");
      }
    },
    wps_doc_meta: function (p) {
      return Promise.resolve().then(function () {
        var lines = ["clientType: wps"];
        try {
          if (!window.wps) {
            lines.push("（WPS 对象不可用）");
            return lines.join("\n");
          }
          if (window.wps.ActiveDocument && window.wps.ActiveDocument.Name) {
            lines.push("文档名: " + window.wps.ActiveDocument.Name);
            if (window.wps.ActiveDocument.FullName) lines.push("路径: " + window.wps.ActiveDocument.FullName);
          } else if (window.wps.ActiveWorkbook && window.wps.ActiveWorkbook.Name) {
            lines.push("工作簿: " + window.wps.ActiveWorkbook.Name);
          } else if (window.wps.Application && window.wps.Application.ActivePresentation) {
            var pr = window.wps.Application.ActivePresentation;
            lines.push("演示文稿: " + (pr.FullName || pr.Name || "(无名)"));
          } else {
            lines.push("（未能识别当前文档类型）");
          }
        } catch (e) {
          lines.push("错误: " + (e && e.message ? e.message : String(e)));
        }
        return lines.join("\n");
      });
    },
    wps_word_body_preview: function (p) {
      return Promise.resolve().then(function () {
        if (!window.wps || !window.wps.Enum || !window.wps.Enum.wdStory) {
          return "wps_word_body_preview 仅适用于 WPS 文字。当前可能不是文字组件。";
        }
        var doc = window.wps.ActiveDocument;
        if (!doc || !doc.Content) return "无法获取正文。";
        var maxLen = wpsDocScriptMaxLen(p, 2000, 32000);
        var t = doc.Content.Text || "";
        var header = "[WPS 正文摘录，最多 " + maxLen + " 字符]\n";
        if (t.length > maxLen) t = t.slice(0, maxLen) + "\n...(已截断)";
        return header + (t || "(无正文)");
      });
    },
    wps_ppt_slide_glance: function (p) {
      return Promise.resolve().then(function () {
        try {
          var app = window.wps && window.wps.Application;
          if (!app) return "WPS Application 不可用。";
          var pres = app.ActivePresentation;
          if (!pres) return "当前不是 WPS 演示，请在 WPS 演示中打开文稿后再试。";
          var win = app.ActiveWindow;
          if (!win || !win.View || !win.View.Slide) return "无法获取当前幻灯片。";
          var slide = win.View.Slide;
          var idx = slide.SlideIndex;
          var n = pres.Slides.Count;
          if (typeof n !== "number") n = n != null && n.value !== undefined ? n.value : 0;
          var parts = [];
          if (slide.Shapes && slide.Shapes.Count) {
            var sc = slide.Shapes.Count;
            for (var si = 1; si <= sc; si++) {
              var sh = slide.Shapes.Item(si);
              if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                var txt = String(sh.TextFrame.TextRange.Text);
                if (txt.length > 800) txt = txt.slice(0, 800) + "...";
                parts.push(txt);
              }
            }
          }
          var body = parts.join(" ").trim() || "(无文本)";
          if (body.length > 8000) body = body.slice(0, 8000) + "...";
          return "[WPS 演示 第 " + idx + " / " + n + " 页]\n" + body;
        } catch (e) {
          return "WPS 幻灯片快览失败：" + (e && e.message ? e.message : String(e));
        }
      });
    }
  };

  function handleRpcRequest(msg) {
    var id = msg.id;
    var method = msg.method;
    var params = msg.params || {};
    if (!id || !method) return;

    function sendRes(result, err) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      ws.send(JSON.stringify({ type: "rpc_response", id: id, result: result != null ? result : null, error: err || null }));
    }

    if (method === "run_document_script") {
      var scriptId = params.scriptId;
      var scriptParams = params.scriptParams || {};
      if (!scriptId || typeof DOCUMENT_SCRIPTS[scriptId] !== "function") {
        sendRes(null, "未知或未注册的脚本 ID: " + (scriptId || ""));
        return;
      }
      Promise.resolve(DOCUMENT_SCRIPTS[scriptId](scriptParams)).then(function (r) {
        sendRes(typeof r === "string" ? r : JSON.stringify(r), null);
      }).catch(function (err) {
        sendRes(null, err && err.message ? err.message : String(err));
      });
      return;
    }

    if (method === "run_custom_document_script") {
      var scriptCode = params.scriptCode;
      if (typeof scriptCode !== "string" || !scriptCode.trim()) {
        sendRes(null, "run_custom_document_script 需要非空的 scriptCode 参数。");
        return;
      }
      try {
        var fn = new Function(scriptCode.trim());
        var out = fn();
        if (out && typeof out.then === "function") {
          out.then(function (r) {
            if (r !== undefined && r !== null && typeof r !== "string") r = JSON.stringify(r);
            sendRes(r === undefined || r === null ? "" : r, null);
          }).catch(function (err) {
            sendRes(null, err && err.message ? err.message : String(err));
          });
        } else {
          if (out !== undefined && out !== null && typeof out !== "string") out = JSON.stringify(out);
          sendRes(out === undefined || out === null ? "" : out, null);
        }
      } catch (err) {
        sendRes(null, err && err.message ? err.message : String(err));
      }
      return;
    }

    if (!window.wps) {
      sendRes(null, "WPS API 不可用，请确保在 WPS 加载项环境中运行。");
      return;
    }

    try {
      if (method === "word_insert_text") {
        var text = params.text != null ? String(params.text) : "";
        if (window.wps.Enum && window.wps.Enum.wdStory) {
          var doc = window.wps.ActiveDocument;
          if (doc && doc.Content) {
            doc.Content.InsertAfter(text);
            sendRes("成功：已在当前 WPS 文档末尾插入内容。", null);
          } else {
            sendRes(null, "无法获取当前文档内容对象。");
          }
        } else {
          sendRes(null, "WPS 文字 API 需在 WPS 文字加载项中调用，请参考 WPS 开放平台文档。");
        }
      } else if (method === "word_read_body") {
        var maxLen = params.maxLength > 0 ? params.maxLength : 8000;
        var doc = window.wps.ActiveDocument;
        if (doc && doc.Content) {
          var t = doc.Content.Text || "";
          if (t.length > maxLen) t = t.slice(0, maxLen) + "\n...(已截断)";
          sendRes(t || "(无正文)", null);
        } else {
          sendRes(null, "无法获取当前文档正文。");
        }
      } else if (method === "word_read_selection") {
        try {
          var sel = window.wps.Selection;
          var selText = (sel && sel.Text) ? sel.Text : "";
          sendRes(selText || "(无选区)", null);
        } catch (e) {
          sendRes(null, "WPS 暂不支持读取选区，请参考 WPS 开放平台 Selection API。");
        }
      } else if (method === "word_insert_table") {
        sendRes(null, "WPS 暂不支持插入表格，请根据 WPS 开放平台 API 实现。");
      } else if (method === "word_search_replace") {
        var searchText = params.searchText != null ? String(params.searchText) : "";
        var replaceText = params.replaceText != null ? String(params.replaceText) : "";
        var replaceAll = params.replaceAll !== false;
        if (!searchText) { sendRes(null, "searchText 不能为空"); return; }
        var doc = window.wps.ActiveDocument;
        if (doc && doc.Content) {
          var fullText = doc.Content.Text || "";
          var newText = replaceAll ? fullText.split(searchText).join(replaceText) : fullText.replace(searchText, replaceText);
          doc.Content.Text = newText;
          sendRes("成功：已完成查找替换。", null);
        } else {
          sendRes(null, "无法获取当前文档内容。");
        }
      } else if (method === "excel_read_range") {
        sendRes(null, "WPS 表格 RPC 需根据 WPS 开放平台 API 实现，请在此处接入 window.wps 对应接口。");
      } else if (method === "excel_write_range") {
        sendRes(null, "WPS 表格 RPC 需根据 WPS 开放平台 API 实现，请在此处接入 window.wps 对应接口。");
      } else if (method === "excel_list_sheets") {
        sendRes(null, "WPS 表格 RPC 需根据 WPS 开放平台 API 实现。");
      } else if (method === "excel_get_used_range") {
        sendRes(null, "WPS 表格 RPC 需根据 WPS 开放平台 API 实现。");
      } else if (method === "excel_read_formulas" || method === "excel_write_formulas") {
        sendRes(null, "WPS 表格公式 RPC 需根据 WPS 开放平台 API 实现。");
      } else if (method === "ppt_slides_list") {
        try {
          var app = window.wps.Application || window.wps;
          var pres = app.ActivePresentation;
          if (!pres) {
            sendRes(null, "当前不是 WPS 演示文档，无法执行 ppt_slides_list。请在 WPS 演示中打开文稿后再试。");
            return;
          }
          var slides = pres.Slides;
          if (!slides) {
            sendRes("演示文稿中无幻灯片。", null);
            return;
          }
          var count = slides.Count;
          if (typeof count !== "number") count = (count != null && count.value !== undefined) ? count.value : 0;
          var out = "共 " + count + " 张幻灯片（按播放顺序）：\n";
          for (var idx = 0; idx < count; idx++) {
            var slide = slides.Item(idx + 1);
            var preview = "(无文本)";
            if (slide && slide.Shapes && slide.Shapes.Count > 0) {
              var parts = [];
              for (var si = 0; si < slide.Shapes.Count; si++) {
                var sh = slide.Shapes.Item(si + 1);
                if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                  var t = String(sh.TextFrame.TextRange.Text).slice(0, 80);
                  if (t.length === 80) t += "...";
                  parts.push(t);
                }
              }
              if (parts.length > 0) preview = parts.join(" ");
            }
            out += "  " + (idx + 1) + ". " + preview + "\n";
          }
          sendRes(out.trim(), null);
        } catch (e) {
          sendRes(null, "当前不是 WPS 演示文档或 API 不可用，无法执行 ppt_slides_list：" + (e && e.message ? e.message : String(e)));
        }
      } else if (method === "ppt_slide_read") {
        try {
          var slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1;
          var includeShapeDetails = params.includeShapeDetails !== false && params.includeShapeDetails !== "false";
          if (slideIndex < 1) {
            sendRes("[错误] slideIndex 必须大于等于 1。", null);
            return;
          }
          var app = window.wps.Application || window.wps;
          var pres = app.ActivePresentation;
          if (!pres) {
            sendRes(null, "当前不是 WPS 演示文档，无法执行 ppt_slide_read。请在 WPS 演示中打开文稿后再试。");
            return;
          }
          var slides = pres.Slides;
          if (!slides) {
            sendRes(null, "演示文稿中无幻灯片。");
            return;
          }
          var count = slides.Count;
          if (typeof count !== "number") count = (count != null && count.value !== undefined) ? count.value : 0;
          if (slideIndex > count) {
            sendRes("[错误] 幻灯片序号 " + slideIndex + " 超出范围，当前共 " + count + " 张。", null);
            return;
          }
          var slide = slides.Item(slideIndex);
          var parts = [];
          var shapeLines = [];
          if (slide && slide.Shapes && slide.Shapes.Count > 0) {
            for (var i = 0; i < slide.Shapes.Count; i++) {
              var sh = slide.Shapes.Item(i + 1);
              if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                var tx = String(sh.TextFrame.TextRange.Text);
                parts.push(tx);
                if (includeShapeDetails) {
                  var nm = sh.Name != null ? String(sh.Name) : "";
                  var pv = tx.length > 120 ? tx.slice(0, 120) + "..." : tx;
                  if (!pv) pv = "(空)";
                  shapeLines.push("  [" + (i + 1) + "] Name=\"" + nm + "\" 预览: " + pv);
                }
              }
            }
          }
          var text = "[幻灯片 " + slideIndex + "]\n" + (parts.length > 0 ? parts.join(" ").trim() : "(无文本)");
          if (includeShapeDetails) {
            text += "\n\n[形状列表（编号供 shapeIndex）]\n";
            text += (shapeLines.length > 0 ? shapeLines.join("\n") : "（本页无带文本的形状）");
          }
          sendRes(text, null);
        } catch (e) {
          sendRes(null, "当前不是 WPS 演示文档或 API 不可用，无法执行 ppt_slide_read：" + (e && e.message ? e.message : String(e)));
        }
      } else if (method === "ppt_slide_write") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1;
          var placeholderType = (params.placeholderType || "title").toString().trim().toLowerCase();
          var text = (params.text != null ? params.text : "").toString();
          var shapeIndex = params.shapeIndex != null ? parseInt(params.shapeIndex, 10) : 0;
          var shapeName = (params.shapeName != null ? params.shapeName : "").toString().trim();
          if (slideIndex < 1) {
            sendRes("[错误] slideIndex 必须大于等于 1。", null);
            return;
          }
          var app = window.wps.Application || window.wps;
          var pres = app.ActivePresentation;
          if (!pres || !pres.Slides) {
            sendRes(null, "当前不是 WPS 演示文档，无法执行 ppt_slide_write。");
            return;
          }
          var count = typeof pres.Slides.Count === "number" ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0);
          if (slideIndex > count) {
            sendRes("[错误] 幻灯片序号 " + slideIndex + " 超出范围。", null);
            return;
          }
          var slide = pres.Slides.Item(slideIndex);
          if (!slide || !slide.Shapes || slide.Shapes.Count < 1) {
            sendRes("[错误] 未找到可写入的形状。", null);
            return;
          }
          var shape = null;
          if (shapeIndex > 0 && shapeIndex <= slide.Shapes.Count) {
            var cand = slide.Shapes.Item(shapeIndex);
            if (cand && cand.TextFrame && cand.TextFrame.TextRange) shape = cand;
          }
          if (!shape && shapeName) {
            for (var si = 0; si < slide.Shapes.Count; si++) {
              var it = slide.Shapes.Item(si + 1);
              if (it && it.Name && String(it.Name).toLowerCase() === shapeName.toLowerCase() && it.TextFrame && it.TextFrame.TextRange) {
                shape = it;
                break;
              }
            }
          }
          if (!shape) {
            var idx = placeholderType === "body" || placeholderType === "subtitle" ? 2 : 1;
            shape = slide.Shapes.Item(idx) || slide.Shapes.Item(1);
          }
          if (!shape || !shape.TextFrame || !shape.TextFrame.TextRange) {
            sendRes("[错误] 未找到可写入的形状，请用 ppt_slide_read 查看形状编号。", null);
            return;
          }
          shape.TextFrame.TextRange.Text = text;
          sendRes("成功：已写入幻灯片文本。", null);
        } catch (e) {
          sendRes(null, "ppt_slide_write 失败：" + (e && e.message ? e.message : String(e)));
        }
      } else if (method === "ppt_slide_insert") {
        try {
          var position = params.position != null ? parseInt(params.position, 10) : null;
          var titleText = (params.titleText != null ? params.titleText : "").toString();
          var bodyText = (params.bodyText != null ? params.bodyText : "").toString();
          var app = window.wps.Application || window.wps;
          var pres = app.ActivePresentation;
          if (!pres || !pres.Slides) {
            sendRes(null, "当前不是 WPS 演示文档，无法执行 ppt_slide_insert。");
            return;
          }
          if (position != null && position < 0) position = null;
          pres.Slides.AddSlide(position != null ? position : -1);
          var count = typeof pres.Slides.Count === "number" ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0);
          var newSlide = pres.Slides.Item(count);
          if (newSlide && newSlide.Shapes) {
            if (newSlide.Shapes.Count >= 1 && newSlide.Shapes.Item(1).TextFrame && newSlide.Shapes.Item(1).TextFrame.TextRange) {
              newSlide.Shapes.Item(1).TextFrame.TextRange.Text = titleText;
            }
            if (newSlide.Shapes.Count >= 2 && newSlide.Shapes.Item(2).TextFrame && newSlide.Shapes.Item(2).TextFrame.TextRange) {
              newSlide.Shapes.Item(2).TextFrame.TextRange.Text = bodyText;
            }
          }
          sendRes("成功：已插入新幻灯片。", null);
        } catch (e) {
          sendRes(null, "ppt_slide_insert 失败：" + (e && e.message ? e.message : String(e)));
        }
      } else if (method === "ppt_slide_delete") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 0;
          if (slideIndex < 1) {
            sendRes("[错误] slideIndex 必须大于等于 1。", null);
            return;
          }
          var app = window.wps.Application || window.wps;
          var pres = app.ActivePresentation;
          if (!pres || !pres.Slides) {
            sendRes(null, "当前不是 WPS 演示文档，无法执行 ppt_slide_delete。");
            return;
          }
          var count = typeof pres.Slides.Count === "number" ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0);
          if (slideIndex > count) {
            sendRes("[错误] 幻灯片序号 " + slideIndex + " 超出范围。", null);
            return;
          }
          var slide = pres.Slides.Item(slideIndex);
          if (slide && typeof slide.Delete === "function") {
            slide.Delete();
          } else {
            sendRes(null, "当前 WPS 版本不支持删除幻灯片。");
            return;
          }
          sendRes("成功：已删除该幻灯片。", null);
        } catch (e) {
          sendRes(null, "ppt_slide_delete 失败：" + (e && e.message ? e.message : String(e)));
        }
      } else if (method === "ppt_document_create") {
        try {
          var filePath = (params.filePath != null ? String(params.filePath) : '').trim()
          if (!filePath) {
            sendRes('[错误] 请提供 filePath（.pptx 或 .pptm）。', null)
            return
          }
          var lower = filePath.toLowerCase()
          if (!lower.endsWith('.pptx') && !lower.endsWith('.pptm')) {
            sendRes('[错误] filePath 须为 .pptx 或 .pptm。', null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.Presentations.Add()
          if (pres && typeof pres.SaveAs === 'function') {
            pres.SaveAs(filePath)
          }
          sendRes('成功：已新建演示文稿并保存到：' + filePath, null)
        } catch (e) {
          sendRes(null, 'ppt_document_create 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_slide_image_add") {
        try {
          var slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1
          var imagePath = (params.imagePath != null ? String(params.imagePath) : '').trim()
          var imageBase64 = params.imageBase64 != null ? String(params.imageBase64) : ''
          var tempFromB64 = null
          if (!imagePath && imageBase64) {
            try {
              var fso = new ActiveXObject("Scripting.FileSystemObject")
              var folder = fso.GetSpecialFolder(2)
              tempFromB64 = folder + '\\taskly_ppt_img_' + Date.now() + '.bin'
              var stream = new ActiveXObject("ADODB.Stream")
              stream.Type = 1
              stream.Open()
              var xml = new ActiveXObject("Microsoft.XMLDOM")
              var el = xml.createElement('tmp')
              el.dataType = 'bin.base64'
              el.text = imageBase64.replace(/\s/g, '')
              stream.Write(el.nodeTypedValue)
              stream.SaveToFile(tempFromB64, 2)
              stream.Close()
              imagePath = tempFromB64
            } catch (e2) {
              sendRes(
                null,
                'ppt_slide_image_add：无法将 imageBase64 写入临时文件（' +
                  (e2 && e2.message ? e2.message : String(e2)) +
                  '）。请提供本机 imagePath，或使用 Chrome + 后端文件路径。'
              )
              return
            }
          }
          if (!imagePath) {
            sendRes(null, '[错误] 请提供 imagePath，或由服务端随 RPC 附带 imageBase64。')
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex < 1 || slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          var left = 20
          var top = 120
          var width = 400
          var height = 225
          if (slide && slide.Shapes && typeof slide.Shapes.AddPicture === 'function') {
            slide.Shapes.AddPicture(imagePath, false, true, left, top, width, height)
            sendRes('成功：已在第 ' + slideIndex + ' 页插入图片。', null)
          } else {
            sendRes(null, '当前 WPS 版本不支持 Shapes.AddPicture。')
          }
          if (tempFromB64) {
            try {
              new ActiveXObject("Scripting.FileSystemObject").DeleteFile(tempFromB64, true)
            } catch (eIgn) {}
          }
        } catch (e) {
          sendRes(null, 'ppt_slide_image_add 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_notes_read") {
        try {
          var slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          var np = slide.NotesPage
          if (!np && typeof slide.AddNotesPage === 'function') {
            slide.AddNotesPage()
            np = slide.NotesPage
          }
          if (!np) {
            sendRes('（无备注）', null)
            return
          }
          var text = wpsPptNotesPlainText(np)
          sendRes(text ? text : '（无备注）', null)
        } catch (e) {
          sendRes(null, 'ppt_notes_read 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_notes_write") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 0
          var text = (params.text != null ? params.text : '').toString()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          var np = slide.NotesPage
          if (!np && typeof slide.AddNotesPage === 'function') {
            slide.AddNotesPage()
            np = slide.NotesPage
          }
          if (!np || !np.Shapes) {
            sendRes(null, '[错误] 无法创建或访问备注页。')
            return
          }
          wpsPptSetNotesPlainText(np, text)
          sendRes('成功：已写入备注。', null)
        } catch (e) {
          sendRes(null, 'ppt_notes_write 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_slides_reorder") {
        try {
          var newOrder = (params.newOrder != null ? String(params.newOrder) : '').trim()
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var n = wpsPptCollectionCount(pres.Slides)
          var parsed = parsePptReorder1Based(newOrder, n)
          if (parsed.err) {
            sendRes(parsed.err, null)
            return
          }
          var order = parsed.order
          var slides = pres.Slides
          var refs = []
          for (var k = 0; k < order.length; k++) {
            refs.push(slides.Item(order[k]))
          }
          for (var targetPos = 1; targetPos <= n; targetPos++) {
            var s = refs[targetPos - 1]
            if (s && typeof s.MoveTo === 'function') {
              s.MoveTo(targetPos)
            } else {
              sendRes(null, '当前 WPS 版本不支持 Slide.MoveTo，无法重排幻灯片。')
              return
            }
          }
          sendRes('成功：已重排幻灯片。', null)
        } catch (e) {
          sendRes(null, 'ppt_slides_reorder 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_table_create") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          var rows = params.rows != null ? parseInt(params.rows, 10) : 2
          var cols = params.cols != null ? parseInt(params.cols, 10) : 2
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          if (rows < 1 || cols < 1 || rows > 20 || cols > 10) {
            sendRes('[错误] 表格行列无效（行 1–20，列 1–10）。', null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          if (!slide || !slide.Shapes || typeof slide.Shapes.AddTable !== 'function') {
            sendRes(null, '当前 WPS 版本不支持 Shapes.AddTable。')
            return
          }
          var left = 36
          var top = 216
          var width = 720
          var height = 200
          slide.Shapes.AddTable(rows, cols, left, top, width, height)
          sendRes('成功：已在第 ' + slideIndex + ' 页添加表格（' + rows + '×' + cols + '）。', null)
        } catch (e) {
          sendRes(null, 'ppt_table_create 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_table_write_cells") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          var rowsCsv = (params.rowsCsv != null ? params.rowsCsv : '').toString()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          var parsedCsv = parsePptRowsCsv(rowsCsv)
          if (parsedCsv.err) {
            sendRes(parsedCsv.err, null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          var tableShape = wpsPptFindFirstTableShape(slide)
          if (!tableShape || !tableShape.Table) {
            sendRes(null, '[错误] 该幻灯片上未找到表格。')
            return
          }
          var tbl = tableShape.Table
          var inputRows = parsedCsv.rows
          for (var r = 0; r < inputRows.length; r++) {
            var rowIdx = r + 1
            var cells = inputRows[r]
            for (var c = 0; c < cells.length; c++) {
              var colIdx = c + 1
              try {
                var cell = tbl.Cell(rowIdx, colIdx)
                if (cell && cell.Shape && cell.Shape.TextFrame && cell.Shape.TextFrame.TextRange) {
                  cell.Shape.TextFrame.TextRange.Text = cells[c]
                }
              } catch (eCell) {}
            }
          }
          sendRes('成功：已写入表格单元格。', null)
        } catch (e) {
          sendRes(null, 'ppt_table_write_cells 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_hyperlink_add") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          var url = (params.url != null ? String(params.url) : '').trim()
          var shapeIndex = params.shapeIndex != null ? parseInt(params.shapeIndex, 10) : 1
          var shapeName = (params.shapeName != null ? params.shapeName : '').toString().trim()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          if (!url) {
            sendRes('[错误] URL 为空。', null)
            return
          }
          if (!/^https?:\/\//i.test(url)) {
            sendRes('[错误] URL 必须是绝对地址（如 https://...）。', null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          var shape = wpsPptPickTextShape(slide, shapeIndex, shapeName)
          if (!shape || !shape.TextFrame || !shape.TextFrame.TextRange) {
            sendRes(null, '[错误] 未找到可设置超链接的文本形状，请检查 shapeIndex 或 shapeName。')
            return
          }
          var clickEnum = app.Enum && app.Enum.ppMouseClick != null ? app.Enum.ppMouseClick : 1
          var aset = shape.ActionSettings(clickEnum)
          if (aset && aset.Hyperlink) {
            aset.Hyperlink.Address = url
            sendRes('成功：已添加超链接。', null)
          } else {
            sendRes(null, '[错误] 无法设置 ActionSettings.Hyperlink。')
          }
        } catch (e) {
          sendRes(null, 'ppt_hyperlink_add 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === "ppt_slide_duplicate") {
        try {
          var slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          var app = window.wps.Application || window.wps
          var pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          var count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          var slide = pres.Slides.Item(slideIndex)
          if (slide && typeof slide.Duplicate === 'function') {
            slide.Duplicate()
            sendRes('成功：已复制幻灯片（插入在源页之后）。', null)
          } else {
            sendRes(null, '当前 WPS 版本不支持 Slide.Duplicate。')
          }
        } catch (e) {
          sendRes(null, 'ppt_slide_duplicate 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else {
        sendRes(null, "Method not supported in this client: " + method);
      }
    } catch (err) {
      console.error("RPC Error:", err);
      sendRes(null, err.message || String(err));
    }
  }

  // HITL：与 chrome-extension/sidepanel.js 对齐（遗留 public 栈）；规范源在 Chrome。优先使用 wps-addin-new/src（Vue）主线时亦应对齐 Chrome，勿以本文件为权威。
  var pendingConfirmId = null;
  var $hitlOverlay = document.getElementById("hitl-overlay");
  var $hitlHumanSummary = document.getElementById("hitl-human-summary");
  var $hitlRawLabel = document.getElementById("hitl-raw-label");
  var $hitlAction = document.getElementById("hitl-action");
  var $hitlAllowBtn = document.getElementById("hitl-allow-btn");
  var $hitlAddToListBtn = document.getElementById("hitl-add-to-list-btn");
  var $hitlDenyBtn = document.getElementById("hitl-deny-btn");

  function handleConfirmRequest(msg) {
    var requestId = msg.id || msg.requestId;
    var action = msg.content || msg.action || "未知操作";
    var humanSummary = (msg.humanSummary && String(msg.humanSummary).trim()) || "";
    var hitlKind = msg.hitlKind;
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
    if ($hitlAddToListBtn) $hitlAddToListBtn.style.display = (hitlKind === "run_command" || hitlKind === "run_page_script") ? "" : "none";
    if ($hitlOverlay) { $hitlOverlay.style.display = "flex"; $hitlOverlay.setAttribute("aria-hidden", "false"); }
  }

  function sendConfirmResponse(id, allowed, addToAllowList) {
    if (!id) return;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "confirm_response", id: id, allowed: allowed, addToAllowList: !!addToAllowList }));
    pendingConfirmId = null;
    // 对齐 chrome-extension/sidepanel.js sendConfirmResponse
    if ($hitlHumanSummary) {
      $hitlHumanSummary.textContent = "";
      $hitlHumanSummary.style.display = "none";
    }
    if ($hitlRawLabel) $hitlRawLabel.style.display = "none";
    if ($hitlOverlay) { $hitlOverlay.style.display = "none"; $hitlOverlay.setAttribute("aria-hidden", "true"); }
  }

  if ($hitlAllowBtn) $hitlAllowBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true, false); });
  if ($hitlAddToListBtn) $hitlAddToListBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true, true); });
  if ($hitlDenyBtn) $hitlDenyBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, false); });

  function handleSend() {
    var text = $input.value.trim();
    if (!text) return;
    if (currentRoundWrapper) return;
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addUserMessage(text);
      pendingMessages.push({ text: text });
      addBotMessage("连接已断开，正在重连… 请确保后台已启动并在 Chrome 扩展中配置。", true);
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
      connect();
      $input.value = "";
      $input.focus();
      return;
    }
    addUserMessage(text);
    send(text);
    $input.value = "";
    $input.focus();
  }

  if ($sendBtn) $sendBtn.addEventListener("click", handleSend);
  if ($stopBtn) {
    $stopBtn.addEventListener("click", function () {
      if (!currentRoundWrapper) return;
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "stop" }));
      finalizeStream();
    });
  }
  if ($input) $input.addEventListener("keydown", function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); handleSend(); } });

  sessionId = getSessionId();
  connect();
})();
