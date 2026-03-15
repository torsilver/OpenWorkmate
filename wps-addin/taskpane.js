(function () {
  "use strict";

  const WS_URL = "ws://localhost:8765/ws";
  const API_BASE = "http://localhost:8765";
  const AUTH_TOKEN = "office-copilot-dev-token";
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 16000;
  const CLIENT_TYPE = "wps";

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
  let streamingBubble = null;

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

  var currentBotMessageRaw = "";
  var currentRoundWrapper = null;
  var executionLogSection = null;
  var executionLogBody = null;
  var executionLogSummaryEl = null;
  var currentRoundToolBlocks = [];
  var currentToolEndIndex = 0;

  function beginStream() {
    var w = $messages.querySelector(".welcome");
    if (w) w.remove();
    currentRoundWrapper = document.createElement("div");
    currentRoundWrapper.className = "msg msg--round";
    streamingBubble = document.createElement("div");
    streamingBubble.className = "msg msg--bot msg--streaming";
    streamingBubble.textContent = "";
    currentRoundWrapper.appendChild(streamingBubble);
    executionLogSection = document.createElement("details");
    executionLogSection.className = "msg msg--execution-log";
    executionLogSummaryEl = document.createElement("summary");
    executionLogSummaryEl.textContent = "执行过程 (0 个操作)";
    executionLogSection.appendChild(executionLogSummaryEl);
    executionLogBody = document.createElement("div");
    executionLogBody.className = "execution-log-body";
    executionLogSection.appendChild(executionLogBody);
    executionLogSection.open = false;
    currentRoundWrapper.appendChild(executionLogSection);
    $messages.appendChild(currentRoundWrapper);
    currentBotMessageRaw = "";
    currentRoundToolBlocks = [];
    currentToolEndIndex = 0;
    setInputEnabled(false);
  }

  function updateExecutionLogCount() {
    if (executionLogSummaryEl) executionLogSummaryEl.textContent = "执行过程 (" + currentRoundToolBlocks.length + " 个操作)";
    if (executionLogSection && currentRoundToolBlocks.length > 0) executionLogSection.open = true;
  }

  function appendStreamChunk(text) {
    if (!streamingBubble) beginStream();
    currentBotMessageRaw += text;
    if (typeof marked !== "undefined") {
      streamingBubble.innerHTML = marked.parse(currentBotMessageRaw);
    } else {
      streamingBubble.textContent = currentBotMessageRaw;
    }
    $messages.scrollTop = $messages.scrollHeight;
  }

  function finalizeStream() {
    if (streamingBubble) {
      streamingBubble.classList.remove("msg--streaming");
      if (typeof marked !== "undefined") streamingBubble.innerHTML = marked.parse(currentBotMessageRaw);
      streamingBubble = null;
      currentBotMessageRaw = "";
    }
    if (executionLogSection) {
      executionLogSection.style.display = currentRoundToolBlocks.length === 0 ? "none" : "";
      if (currentRoundToolBlocks.length > 0) {
        executionLogSection.open = false;
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
    var url = WS_URL + "?sessionId=" + encodeURIComponent(sessionId) + "&token=" + encodeURIComponent(AUTH_TOKEN) + "&clientType=" + encodeURIComponent(CLIENT_TYPE);
    ws = new WebSocket(url);
    ws.onopen = function () {
      reconnectDelay = RECONNECT_BASE_MS;
      setStatus(true);
      addSystemMessage("已连接到本地服务");
      while (pendingMessages.length > 0) {
        var m = pendingMessages.shift();
        send(m.text);
      }
    };
    ws.onmessage = function (e) { handleMessage(e.data); };
    ws.onclose = function () {
      var wasStreaming = !!streamingBubble;
      ws = null;
      setStatus(false);
      finalizeStream();
      if (wasStreaming) addBotMessage("连接已断开，请检查网络或稍后重试。", true);
      scheduleReconnect();
    };
    ws.onerror = function () { ws.close(); };
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(function () {
      reconnectTimer = null;
      reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
      connect();
    }, reconnectDelay);
  }

  function send(text) {
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage("连接已断开，消息未发送。请检查后台是否启动，并在 Chrome 扩展中配置。", true);
      return;
    }
    var payload = { type: "text", content: text || "" };
    if (currentPlanId) {
      payload.mode = "agent";
      payload.planId = currentPlanId;
    }
    ws.send(JSON.stringify(payload));
  }

  function fetchPlanAndShow(planId, title, createdBy) {
    if (createdBy !== CLIENT_TYPE) return;
    fetch(API_BASE + "/api/plans/" + encodeURIComponent(planId))
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
      case "stream_start": beginStream(); break;
      case "stream_chunk": appendStreamChunk(msg.content); break;
      case "stream_end": finalizeStream(); break;
      case "tool_invocation_start":
        if (!executionLogBody) break;
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
        executionLogBody.appendChild(block);
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
      case "plan_created":
      case "plan_updated": {
        var planId = msg.planId || "";
        var title = msg.title || "新计划";
        var createdBy = (msg.createdBy || "").toLowerCase();
        if (planId && createdBy === CLIENT_TYPE) fetchPlanAndShow(planId, title, createdBy);
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
      fetch(API_BASE + "/api/plans/" + encodeURIComponent(currentPlanId), {
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
  }

  var DOCUMENT_SCRIPTS = {
    word_read_selection: function (p) {
      if (!window.wps || !window.wps.ActiveDocument) return Promise.resolve("当前环境不可用。");
      try {
        var sel = window.wps.Selection;
        var t = (sel && sel.Text) ? sel.Text : "";
        return Promise.resolve(t || "(无选区)");
      } catch (e) {
        return Promise.resolve("WPS 暂不支持读取选区，请使用 word_read_body。");
      }
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
      } else {
        sendRes(null, "Method not supported in this client: " + method);
      }
    } catch (err) {
      console.error("RPC Error:", err);
      sendRes(null, err.message || String(err));
    }
  }

  var pendingConfirmId = null;
  var $hitlOverlay = document.getElementById("hitl-overlay");
  var $hitlAction = document.getElementById("hitl-action");
  var $hitlAllowBtn = document.getElementById("hitl-allow-btn");
  var $hitlDenyBtn = document.getElementById("hitl-deny-btn");

  function handleConfirmRequest(msg) {
    var requestId = msg.id || msg.requestId;
    var action = msg.content || msg.action || "未知操作";
    if (!requestId) return;
    pendingConfirmId = requestId;
    if ($hitlAction) $hitlAction.textContent = action;
    if ($hitlOverlay) { $hitlOverlay.style.display = "flex"; $hitlOverlay.setAttribute("aria-hidden", "false"); }
  }

  function sendConfirmResponse(id, allowed) {
    if (!id) return;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "confirm_response", id: id, allowed: allowed }));
    pendingConfirmId = null;
    if ($hitlOverlay) { $hitlOverlay.style.display = "none"; $hitlOverlay.setAttribute("aria-hidden", "true"); }
  }

  if ($hitlAllowBtn) $hitlAllowBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true); });
  if ($hitlDenyBtn) $hitlDenyBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, false); });

  function handleSend() {
    var text = $input.value.trim();
    if (!text) return;
    if (streamingBubble) return;
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
      if (!streamingBubble) return;
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "stop" }));
      finalizeStream();
    });
  }
  if ($input) $input.addEventListener("keydown", function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); handleSend(); } });

  sessionId = getSessionId();
  connect();
})();
