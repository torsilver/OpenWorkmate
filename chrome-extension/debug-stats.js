(function () {
  "use strict";

  let API_BASE = "http://127.0.0.1:8765";

  const $err = document.getElementById("err");
  const $dl = document.getElementById("tool-selection-dl");
  const $toolsWrap = document.getElementById("tools-table-wrap");
  const $last = document.getElementById("last-updated");
  const $btnRefresh = document.getElementById("btn-refresh");
  const $btnReset = document.getElementById("btn-reset");
  const $chkAuto = document.getElementById("chk-auto");
  const $cfg = document.getElementById("tool-search-config");
  const $histWrap = document.getElementById("histogram-wrap");
  const $clientWrap = document.getElementById("client-type-wrap");

  let autoTimer = null;

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

  function fmtFixed(x, digits) {
    if (x == null || Number.isNaN(x)) return "—";
    return Number(x).toFixed(digits);
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

  function renderToolSearchConfig(cfg) {
    if (!$cfg) return;
    if (!cfg || typeof cfg !== "object") {
      $cfg.textContent = "";
      return;
    }
    $cfg.innerHTML =
      "当前 <code>ContextWindow</code> 工具检索配置：<code>toolSearchTopK</code>=" +
      escapeHtml(String(cfg.toolSearchTopK ?? "—")) +
      "，<code>toolSearchMinScore</code>=" +
      escapeHtml(String(cfg.toolSearchMinScore ?? "—")) +
      "，<code>toolSearchMinCount</code>=" +
      escapeHtml(String(cfg.toolSearchMinCount ?? "—")) +
      "（与下方直方图、GoodEnough 统计对照用）。";
  }

  function renderHistogram(buckets, vecRuns) {
    if (!$histWrap) return;
    if (!Array.isArray(buckets) || buckets.length === 0) {
      $histWrap.innerHTML = "<p class=\"empty\">尚无向量检索记录。</p>";
      return;
    }
    const total = typeof vecRuns === "number" && vecRuns > 0 ? vecRuns : buckets.reduce((s, b) => s + (b.count || 0), 0);
    const table = document.createElement("table");
    table.innerHTML = "<thead><tr><th>分数区间</th><th class=\"num\">次数</th><th class=\"num\">占检索次数</th></tr></thead><tbody></tbody>";
    const tbody = table.querySelector("tbody");
    for (const b of buckets) {
      const c = b.count ?? 0;
      const p = total > 0 ? pct(c / total) : "—";
      const tr = document.createElement("tr");
      tr.innerHTML =
        "<td>" + escapeHtml(b.label || "") + "</td>" +
        "<td class=\"num\">" + c + "</td>" +
        "<td class=\"num\">" + p + "</td>";
      tbody.appendChild(tr);
    }
    $histWrap.innerHTML = "";
    $histWrap.appendChild(table);
  }

  function renderClientTypes(rows) {
    if (!$clientWrap) return;
    if (!Array.isArray(rows) || rows.length === 0) {
      $clientWrap.innerHTML = "<p class=\"empty\">尚无按客户端分类的向量检索记录。</p>";
      return;
    }
    const table = document.createElement("table");
    table.innerHTML = "<thead><tr><th>clientType</th><th class=\"num\">向量检索次数</th><th class=\"num\">平均最高分</th></tr></thead><tbody></tbody>";
    const tbody = table.querySelector("tbody");
    for (const r of rows) {
      const tr = document.createElement("tr");
      const avg = r.averageMaxScore != null ? fmtFixed(r.averageMaxScore, 4) : "—";
      tr.innerHTML =
        "<td>" + escapeHtml(r.clientType || "") + "</td>" +
        "<td class=\"num\">" + (r.vectorSearchRunCount ?? 0) + "</td>" +
        "<td class=\"num\">" + avg + "</td>";
      tbody.appendChild(tr);
    }
    $clientWrap.innerHTML = "";
    $clientWrap.appendChild(table);
  }

  function render(data) {
    showErr("");
    const ts = data.toolSelection || {};
    renderToolSearchConfig(data.toolSearchConfig);
    renderHistogram(ts.maxScoreHistogram, ts.vectorSearchRunCount);
    renderClientTypes(ts.vectorSearchByClientType);

    if ($dl) {
      $dl.innerHTML = "";
      const entries = [
        ["服务启动时间 (UTC)", data.serverStartedUtc ? String(data.serverStartedUtc) : "—"],
        ["累计统计起始时间 (UTC)", data.statsAccumulatedSinceUtc ? String(data.statsAccumulatedSinceUtc) : "—"],
        ["非计划模式工具选择总次数", String(ts.totalNonPlanSelections ?? 0)],
        ["选择阶段异常回退全量工具", String(ts.selectionExceptionFallbackCount ?? 0)],
        ["向量检索跳过（未配置 Embedding）", String(ts.vectorSkippedNoEmbeddingCount ?? 0)],
        ["向量检索跳过（存储非持久化）", String(ts.vectorSkippedNonPersistentStoreCount ?? 0)],
        ["实际执行向量检索次数", String(ts.vectorSearchRunCount ?? 0)],
        ["其中 GoodEnough==true 次数", String(ts.vectorGoodEnoughTrueCount ?? 0)],
        ["其中 GoodEnough==false 次数", String(ts.vectorGoodEnoughFalseCount ?? 0)],
        ["GoodEnough==true 但命中数为 0（异常边界）", String(ts.vectorGoodEnoughTrueButEmptyResultsCount ?? 0)],
        ["采用向量优先路径次数", String(ts.vectorFirstPathChosenCount ?? 0)],
        ["向量检索后仍走两阶段次数", String(ts.vectorSearchButTwoStageCount ?? 0)],
        ["两阶段选择调用次数", String(ts.twoStageInvocationsCount ?? 0)],
        ["向量检索：平均最高分 (maxScore)", fmtFixed(ts.averageMaxScoreAmongVectorSearches, 4)],
        ["向量检索：平均去重命中数", fmtFixed(ts.averageDistinctHitCountAmongVectorSearches, 2)],
        ["向量检索：平均 Top1−Top2（样本数）", (() => {
          const avg = fmtFixed(ts.averageTop1MinusTop2AmongVectorSearches, 4);
          const n = ts.top1MinusTop2SampleCount ?? 0;
          return avg + "（n=" + n + "）";
        })()],
        ["GoodEnough==false / 向量检索次数", pct(ts.vectorGoodEnoughFalseRateAmongVectorSearches)],
        ["向量优先 / 向量检索次数", pct(ts.vectorFirstPathRateAmongVectorSearches)],
        ["向量优先 / 非计划选择总次数", pct(ts.vectorFirstPathRateAmongSelections)],
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

  async function load() {
    try {
      const res = await fetch(API_BASE + "/api/debug/agent-stats");
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
    if (!confirm("确定清空所有调试计数？将删除本机持久化文件，工具调用与向量选择统计一并归零。")) return;
    try {
      const res = await fetch(API_BASE + "/api/debug/agent-stats/reset", { method: "POST" });
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

  if ($btnRefresh) $btnRefresh.addEventListener("click", () => load());
  if ($btnReset) $btnReset.addEventListener("click", () => reset());
  if ($chkAuto) {
    $chkAuto.addEventListener("change", () => setAuto($chkAuto.checked));
  }

  TasklyLocalService.tasklyResolveLocalServiceBase(null)
    .then(function (r) {
      API_BASE = TasklyLocalService.normalizeBase(r.baseUrl);
      return load();
    })
    .catch(function (e) {
      showErr(e.message || String(e));
    });
})();
