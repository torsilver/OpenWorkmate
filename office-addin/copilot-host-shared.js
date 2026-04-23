/**
 * Office 加载项与 Chrome/WPS 共用的纯函数（无宿主依赖）。
 * WPS 侧 ESM 镜像：wps-addin-new/src/lib/copilotHostShared.js（修改时请对齐两端）。
 */
(function (g) {
  "use strict";

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

  g.TasklyCopilotHostShared = {
    toolInvocationContentLooksLikeError: toolInvocationContentLooksLikeError,
    buildWebSocketQueryString: buildWebSocketQueryString
  };
})(typeof globalThis !== "undefined" ? globalThis : window);
