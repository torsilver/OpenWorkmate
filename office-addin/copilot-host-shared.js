/**
 * Office 加载项与 Chrome/WPS 共用的纯函数（无宿主依赖）。
 * WPS 侧 ESM 镜像：wps-addin-new/src/lib/copilotHostShared.js（修改时请对齐两端）。
 */
(function (g) {
  "use strict";

  /** 将 JSON 字面量 \\uXXXX 还原为字符（工具参数/结果里常见 ASCII-only JSON） */
  function decodeJsonStyleUnicodeEscapes(s) {
    if (s == null || typeof s !== "string") return s;
    if (s.indexOf("\\u") === -1) return s;
    var t = s;
    var prev = "";
    var guard = 0;
    while (t !== prev && t.indexOf("\\u") !== -1 && guard < 24) {
      prev = t;
      t = t.replace(/\\u([0-9a-fA-F]{4})/g, function (_, h) {
        return String.fromCharCode(parseInt(h, 16));
      });
      guard++;
    }
    return t;
  }

  /** 与 Chrome sidepanel / WPS useCopilot 同源：tool_invocation_end 兜底判定 */
  function toolInvocationContentLooksLikeError(c) {
    if (!c) return false;
    return (
      c.startsWith("[错误]") ||
      c.startsWith("[保存失败]") ||
      c.startsWith("[记忆未启用]") ||
      c.startsWith("[无效]") ||
      c.startsWith("[MCP Error]") ||
      c.startsWith("[MCP Client Exception]") ||
      c.startsWith("[系统拦截]") ||
      c.startsWith("[检索失败]") ||
      c.startsWith("[创建失败]") ||
      c.startsWith("[更新失败]") ||
      c.startsWith("[生成计划失败]") ||
      c.startsWith("[执行步骤失败]") ||
      c.startsWith("[工具调用失败]") ||
      c.startsWith("[参数绑定失败]") ||
      c.startsWith("Error: Function failed.") ||
      c.startsWith("Error: Requested function") ||
      c.startsWith("Error: Unknown error.")
    );
  }

  var DEFAULT_TELEMETRY_TIER = "full";
  var DEFAULT_TELEMETRY_INGEST_LOG_LEVEL = "information";

  /**
   * @param {object} o
   * @param {string} o.sessionId
   * @param {string} o.clientType
   * @param {string} o.agentProfileId
   * @param {string} [o.token]
   * @param {object|null} [o.bootstrap] local-service-auth JSON
   * @param {() => Storage|null} [o.getStorage] default localStorage
   */
  function buildWebSocketQueryString(o) {
    o = o || {};
    var getStorage = o.getStorage || function () {
      try {
        return typeof localStorage !== "undefined" ? localStorage : null;
      } catch (e) {
        return null;
      }
    };
    var st = getStorage();
    function get(key) {
      try {
        return st ? String(st.getItem(key) || "").trim() : "";
      } catch (e2) {
        return "";
      }
    }
    function set(key, val) {
      try {
        if (st) st.setItem(key, val);
      } catch (e3) { /* ignore */ }
    }

    var deviceKey = o.telemetryDeviceIdKey || "tasklyTelemetryDeviceId";
    var emissionKey = o.telemetryClientEmissionKey || "tasklyTelemetryClientEmission";
    var relayActiveKey = o.telemetryRelayActiveProfileKey || "tasklyTelemetryRelayActiveProfileId";
    var kindsByProfileKey = o.telemetryEventKindsByProfileKey || "tasklyTelemetryEventKindsByProfile";

    var devId = get(deviceKey);
    if (!devId) {
      devId = typeof crypto !== "undefined" && crypto.randomUUID ? crypto.randomUUID() : "tid-" + String(Date.now());
      set(deviceKey, devId);
    }

    var tier = DEFAULT_TELEMETRY_TIER;
    var ingestLv = DEFAULT_TELEMETRY_INGEST_LOG_LEVEL;
    var clientEmission = get(emissionKey) || "on";
    if (!clientEmission) clientEmission = "on";

    var serverAllowsObs = true;
    var bootstrap = o.bootstrap;
    if (bootstrap && typeof bootstrap.telemetryUserObservabilityEnabled === "boolean") {
      serverAllowsObs = bootstrap.telemetryUserObservabilityEnabled;
    } else {
      serverAllowsObs = clientEmission !== "off";
    }

    var qs = new URLSearchParams();
    qs.set("sessionId", o.sessionId || "");
    qs.set("clientType", o.clientType || "");
    qs.set("agentProfileId", (o.agentProfileId && String(o.agentProfileId).trim()) || "default");
    if (o.token) qs.set("token", String(o.token));

    if (serverAllowsObs && tier !== "off") {
      qs.set("deviceId", devId);
      qs.set("telemetryTier", tier);
      qs.set("telemetryIngestLogLevel", ingestLv);
      var relayActive = get(relayActiveKey) || "default";
      if (!relayActive) relayActive = "default";
      var kindsMap = {};
      try {
        var raw = st ? st.getItem(kindsByProfileKey) : "";
        if (raw) kindsMap = JSON.parse(raw);
      } catch (e4) {
        kindsMap = {};
      }
      var eventKinds = kindsMap && kindsMap[relayActive];
      if (Array.isArray(eventKinds) && eventKinds.length > 0) {
        qs.set("telemetryEventKinds", eventKinds.join(","));
      }
    }

    return "?" + qs.toString();
  }

  var DEFAULT_CONTEXT_TOKEN_BUDGET = 131072;

  function parseStreamUsagePayload(content) {
    try {
      var raw = typeof content === "string" ? content : String(content || "");
      if (!raw.trim()) return null;
      var u = JSON.parse(raw);
      var prompt =
        typeof u.prompt_tokens === "number"
          ? u.prompt_tokens
          : typeof u.PromptTokens === "number"
            ? u.PromptTokens
            : null;
      var completion =
        typeof u.completion_tokens === "number"
          ? u.completion_tokens
          : typeof u.CompletionTokens === "number"
            ? u.CompletionTokens
            : null;
      var total =
        typeof u.total_tokens === "number"
          ? u.total_tokens
          : typeof u.TotalTokens === "number"
            ? u.TotalTokens
            : null;
      return { promptTokens: prompt, completionTokens: completion, totalTokens: total };
    } catch (e) {
      return null;
    }
  }

  function usagePromptFillRatio(parsed, budget) {
    budget = budget || DEFAULT_CONTEXT_TOKEN_BUDGET;
    if (!parsed || parsed.promptTokens == null || parsed.promptTokens < 0 || !budget || budget <= 0) return null;
    return Math.min(1, parsed.promptTokens / budget);
  }

  function buildStreamUsageRingTitle(parsed, budget) {
    budget = budget || DEFAULT_CONTEXT_TOKEN_BUDGET;
    if (!parsed) return "";
    var parts = [];
    if (parsed.promptTokens != null) parts.push("输入 tokens: " + parsed.promptTokens);
    if (parsed.completionTokens != null) parts.push("输出 tokens: " + parsed.completionTokens);
    if (parsed.totalTokens != null) parts.push("合计: " + parsed.totalTokens);
    parts.push("圆环：输入 / 参考预算 " + budget + "（模型真实上限以服务商为准）");
    return parts.join("\n");
  }

  g.TasklyCopilotHostShared = {
    decodeJsonStyleUnicodeEscapes: decodeJsonStyleUnicodeEscapes,
    toolInvocationContentLooksLikeError: toolInvocationContentLooksLikeError,
    buildWebSocketQueryString: buildWebSocketQueryString,
    DEFAULT_CONTEXT_TOKEN_BUDGET: DEFAULT_CONTEXT_TOKEN_BUDGET,
    parseStreamUsagePayload: parseStreamUsagePayload,
    usagePromptFillRatio: usagePromptFillRatio,
    buildStreamUsageRingTitle: buildStreamUsageRingTitle
  };
})(typeof globalThis !== "undefined" ? globalThis : window);
