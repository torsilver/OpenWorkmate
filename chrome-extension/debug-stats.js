(function () {
  "use strict";

  let API_BASE = "http://127.0.0.1:8765";
  const COPILOT_TOKEN_STORAGE_KEY = "localServiceAuthToken";

  const $err = document.getElementById("err");
  const $dl = document.getElementById("tool-selection-dl");
  const $toolsWrap = document.getElementById("tools-table-wrap");
  const $last = document.getElementById("last-updated");
  const $btnRefresh = document.getElementById("btn-refresh");
  const $btnReset = document.getElementById("btn-reset");
  const $chkAuto = document.getElementById("chk-auto");

  let autoTimer = null;
  let tasklyDebugStatsApiReady = null;

  function tasklyEnsureDebugStatsApiBase() {
    if (tasklyDebugStatsApiReady) return tasklyDebugStatsApiReady;
    tasklyDebugStatsApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(
      typeof chrome !== "undefined" && chrome.storage && chrome.storage.local ? chrome.storage.local : null
    )
      .then(function (r) {
        API_BASE = TasklyLocalService.normalizeBase(r.baseUrl);
      })
      .catch(function (err) {
        tasklyDebugStatsApiReady = null;
        throw err;
      });
    return tasklyDebugStatsApiReady;
  }

  function showErr(msg) {
    if (!$err) return;
    if (!msg) {
      $err.classList.remove("show");
      $err.textContent = "";
      return;
    }
    $err.textContent = msg;
    $err.classList.add("show");
  }

  async function parseErrorMessage(res) {
    try {
      const data = await res.json();
      if (data && typeof data.message === "string" && data.message.trim())
        return data.message.trim();
    } catch (_) { /* ignore */ }
    return `HTTP ${res.status} ${res.statusText || ""}`.trim();
  }

  function pct(x) {
    if (x == null || Number.isNaN(x)) return "—";
    return (x * 100).toFixed(1) + "%";
  }

  function row(dt, dd) {
    const dEl = document.createElement("dt");
    dEl.textContent = dt;
    const ddEl = document.createElement("dd");
    if (typeof dd === "string" && dd.includes("%")) {
      ddEl.innerHTML = "";
      const span = document.createElement("span");
      span.className = "rate";
      span.textContent = dd;
      ddEl.appendChild(span);
    } else {
      ddEl.textContent = dd;
    }
    return [dEl, ddEl];
  }

  function render(data) {
    showErr("");
    const ts = data.toolSelection || {};

    if ($dl) {
      $dl.innerHTML = "";
      const entries = [
        ["服务启动时间 (UTC)", data.serverStartedUtc ? String(data.serverStartedUtc) : "—"],
        ["累计统计起始时间 (UTC)", data.statsAccumulatedSinceUtc ? String(data.statsAccumulatedSinceUtc) : "—"],
        ["非计划模式工具选择总次数", String(ts.totalNonPlanSelections ?? 0)],
        ["选择阶段异常回退全量工具", String(ts.selectionExceptionFallbackCount ?? 0)],
        ["工具需求门控：主模型调用次数", String(ts.toolNeedGateLlmInvocationCount ?? 0)],
        ["其中判定闲聊（本轮不绑工具）", String(ts.toolNeedGateChatOnlyCount ?? 0)],
        ["闲聊占比 / 门控 LLM 次数", pct(ts.toolNeedGateChatOnlyRateAmongGateLlm)],
        ["两阶段选择调用次数", String(ts.twoStageInvocationsCount ?? 0)],
        ["两阶段 / 非计划选择总次数", pct(ts.twoStageRateAmongSelections)]
      ];
      for (const [dt, dd] of entries) {
        const [dEl, ddEl] = row(dt, dd);
        $dl.appendChild(dEl);
        $dl.appendChild(ddEl);
      }
    }

    const tools = data.toolInvocations || [];
    if ($toolsWrap) {
      if (tools.length === 0) {
        $toolsWrap.innerHTML = "<p class=\"empty\">尚无工具调用记录（与模型对话并触发工具后刷新）。</p>";
      } else {
        const table = document.createElement("table");
        table.innerHTML = "<thead><tr><th>工具</th><th class=\"num\">成功</th><th class=\"num\">失败</th><th class=\"num\">合计</th><th class=\"num\">成功率</th></tr></thead><tbody></tbody>";
        const tbody = table.querySelector("tbody");
        for (const t of tools) {
          const tr = document.createElement("tr");
          const rate = t.successRate != null ? pct(t.successRate) : "—";
          tr.innerHTML =
            "<td>" + escapeHtml(t.toolId || "") + "</td>" +
            "<td class=\"num\">" + (t.successCount ?? 0) + "</td>" +
            "<td class=\"num\">" + (t.failCount ?? 0) + "</td>" +
            "<td class=\"num\">" + (t.totalCalls ?? 0) + "</td>" +
            "<td class=\"num\">" + rate + "</td>";
          tbody.appendChild(tr);
        }
        $toolsWrap.innerHTML = "";
        $toolsWrap.appendChild(table);
      }
    }

    if ($last) {
      $last.textContent = "上次更新：" + new Date().toLocaleString("zh-CN", { hour12: false });
    }
  }

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function tasklyFetch(url, init) {
    init = init ? Object.assign({}, init) : {};
    return new Promise(function (resolve) {
      if (typeof chrome === "undefined" || !chrome.storage || !chrome.storage.local) {
        resolve(fetch(url, init));
        return;
      }
      chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (r) {
        var t = (r && r[COPILOT_TOKEN_STORAGE_KEY] || "").trim();
        var headers = Object.assign({}, init.headers || {});
        if (t) headers["X-OfficeCopilot-Token"] = t;
        init.headers = headers;
        resolve(fetch(url, init));
      });
    });
  }

  function ensureLocalServiceTokenFromBootstrap(apiBase) {
    if (typeof chrome === "undefined" || !chrome.storage || !chrome.storage.local)
      return Promise.resolve();
    var base = (apiBase || "").replace(/\/$/, "");
    return fetch(base + "/api/bootstrap/local-service-auth")
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
  }

  function resolveBootstrapAndLoad() {
    return tasklyEnsureDebugStatsApiBase()
      .then(function () {
        return ensureLocalServiceTokenFromBootstrap(API_BASE);
      })
      .then(function () {
        return load();
      })
      .catch(function (e) {
        showErr(e.message || String(e));
      });
  }

  async function load() {
    try {
      const res = await tasklyFetch(API_BASE + "/api/debug/agent-stats");
      if (!res.ok) {
        showErr(await parseErrorMessage(res));
        return;
      }
      const data = await res.json();
      render(data);
    } catch (e) {
      showErr("无法连接本地服务（" + API_BASE + "）：" + (e && e.message ? e.message : String(e)));
    }
  }

  async function reset() {
    if (!confirm("确定清空所有调试计数？将删除本机持久化文件。")) return;
    try {
      await tasklyEnsureDebugStatsApiBase();
      await ensureLocalServiceTokenFromBootstrap(API_BASE);
      const res = await tasklyFetch(API_BASE + "/api/debug/agent-stats/reset", { method: "POST" });
      if (!res.ok) {
        showErr(await parseErrorMessage(res));
        return;
      }
      await load();
    } catch (e) {
      showErr("清空失败：" + (e && e.message ? e.message : String(e)));
    }
  }

  function setAuto(on) {
    if (autoTimer) {
      clearInterval(autoTimer);
      autoTimer = null;
    }
    if (on) autoTimer = setInterval(load, 3000);
  }

  if ($btnRefresh) $btnRefresh.addEventListener("click", () => resolveBootstrapAndLoad());
  if ($btnReset) $btnReset.addEventListener("click", () => reset());
  if ($chkAuto) {
    $chkAuto.addEventListener("change", () => setAuto($chkAuto.checked));
  }

  resolveBootstrapAndLoad();
})();
