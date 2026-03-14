(function () {
  "use strict";

  const WS_URL = "ws://localhost:8765/ws";
  const API_BASE = "http://localhost:8765";
  const AUTH_TOKEN = "office-copilot-dev-token";
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 16000;

  let OFFICE_CLIENT_TYPE = "office"; // set after Office.onReady to office-word | office-excel

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

  let currentPlanId = null;
  let currentPlanTitle = null;
  let currentPlanContent = null;
  let currentPlanCreatedBy = null;

  let ws = null;
  let sessionId = null;
  let reconnectDelay = RECONNECT_BASE_MS;
  let reconnectTimer = null;
  let pendingMessages = [];
  let streamingBubble = null;

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

  let currentBotMessageRaw = "";
  let currentRoundWrapper = null;
  let executionLogSection = null;
  let executionLogBody = null;
  let executionLogSummaryEl = null;
  let currentRoundToolBlocks = [];
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
      if (typeof marked !== "undefined") {
        streamingBubble.innerHTML = marked.parse(currentBotMessageRaw);
      }
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
    const url = WS_URL + "?sessionId=" + encodeURIComponent(sessionId) + "&token=" + encodeURIComponent(AUTH_TOKEN) + "&clientType=" + encodeURIComponent(OFFICE_CLIENT_TYPE);
    ws = new WebSocket(url);

    ws.onopen = function () {
      reconnectDelay = RECONNECT_BASE_MS;
      setStatus(true);
      addSystemMessage("已连接到本地服务");
      while (pendingMessages.length > 0) {
        const m = pendingMessages.shift();
        send(m.text);
      }
    };

    ws.onmessage = function (e) { handleMessage(e.data); };

    ws.onclose = function () {
      const wasStreaming = !!streamingBubble;
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
    const payload = { type: "text", content: text || "" };
    if (currentPlanId) {
      payload.mode = "agent";
      payload.planId = currentPlanId;
    }
    ws.send(JSON.stringify(payload));
  }

  async function fetchPlanAndShow(planId, title, createdBy) {
    if (createdBy !== OFFICE_CLIENT_TYPE) return;
    try {
      const res = await fetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
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
      case "stream_start":
        beginStream();
        break;
      case "stream_chunk":
        appendStreamChunk(msg.content);
        break;
      case "stream_end":
        finalizeStream();
        break;
      case "tool_invocation_start": {
        if (!executionLogBody) break;
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
        executionLogBody.appendChild(block);
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
        const res = await fetch(API_BASE + "/api/plans/" + encodeURIComponent(currentPlanId), {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ content })
        });
        if (!res.ok) throw new Error("保存失败");
        currentPlanContent = content;
        if ($planContentView) {
          $planContentView.innerHTML = (typeof marked !== "undefined") ? marked.parse(content || "") : escapeHtml(content || "");
        }
        cancelPlanEdit();
      } catch (e) {
        console.error("save plan failed", e);
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
      } else if (OFFICE_CLIENT_TYPE === "office-word") {
        if (method === "word_insert_text") {
          const text = params.text != null ? String(params.text) : "";
          await Word.run(function (context) {
            const body = context.document.body;
            body.insertParagraph(text, "End");
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
            body.insertTable(rowCount, columnCount, insertLocation, values || null);
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
  const $hitlDenyBtn = document.getElementById("hitl-deny-btn");

  function handleConfirmRequest(msg) {
    const requestId = msg.id || msg.requestId;
    const action = msg.content || msg.action || "未知操作";
    if (!requestId) return;
    pendingConfirmId = requestId;
    if ($hitlAction) $hitlAction.textContent = action;
    if ($hitlOverlay) {
      $hitlOverlay.style.display = "flex";
      $hitlOverlay.setAttribute("aria-hidden", "false");
    }
  }

  function sendConfirmResponse(id, allowed) {
    if (!id) return;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: "confirm_response", id: id, allowed: allowed }));
    }
    pendingConfirmId = null;
    if ($hitlOverlay) {
      $hitlOverlay.style.display = "none";
      $hitlOverlay.setAttribute("aria-hidden", "true");
    }
  }

  if ($hitlAllowBtn) $hitlAllowBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, true); });
  if ($hitlDenyBtn) $hitlDenyBtn.addEventListener("click", function () { if (pendingConfirmId) sendConfirmResponse(pendingConfirmId, false); });

  function handleSend() {
    const text = $input.value.trim();
    if (!text) return;
    if (streamingBubble) return;

    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addUserMessage(text);
      pendingMessages.push({ text: text });
      addBotMessage("连接已断开，正在重连… 请确保后台已启动并在 Chrome 扩展中配置。", true);
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
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
  if ($input) {
    $input.addEventListener("keydown", function (e) {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        handleSend();
      }
    });
  }

  function boot() {
    sessionId = getSessionId();
    if (typeof Office !== "undefined" && Office.context && Office.context.host) {
      var host = Office.context.host;
      if (host === Office.HostType.Word) OFFICE_CLIENT_TYPE = "office-word";
      else if (host === Office.HostType.Excel) OFFICE_CLIENT_TYPE = "office-excel";
    }
    connect();
  }

  if (typeof Office !== "undefined") {
    Office.onReady(function () { boot(); });
  } else {
    boot();
  }
})();
