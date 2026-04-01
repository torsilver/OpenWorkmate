(function () {
  "use strict";

  let API_BASE = "http://127.0.0.1:8765";
  let tasklyPlansApiReady = null;
  const COPILOT_TOKEN_STORAGE_KEY = "localServiceAuthToken";
  const STORAGE_EXECUTE_PLAN_ID = "copilot_execute_plan_id";
  const STORAGE_EXECUTE_PLAN_TITLE = "copilot_execute_plan_title";

  const $loading = document.getElementById("plans-loading");
  const $error = document.getElementById("plans-error");
  const $detail = document.getElementById("plans-detail");
  const $planTitle = document.getElementById("plan-title");
  const $contentView = document.getElementById("plan-content-view");
  const $contentEditWrap = document.getElementById("plan-content-edit-wrap");
  const $contentEdit = document.getElementById("plan-content-edit");
  const $usePlanBtn = document.getElementById("use-plan-btn");
  const $editBtn = document.getElementById("edit-plan-btn");
  const $saveBtn = document.getElementById("save-plan-btn");
  const $cancelBtn = document.getElementById("cancel-edit-btn");
  const $executePlanBtn = document.getElementById("execute-plan-btn");
  const $executeHint = document.getElementById("execute-hint");

  let currentPlanId = null;
  let currentPlan = null;

  function getPlanIdFromUrl() {
    const params = new URLSearchParams(window.location.search);
    return params.get("id") || params.get("highlight") || "";
  }

  function showLoading(show) {
    if ($loading) $loading.style.display = show ? "block" : "none";
  }

  function showError(message) {
    showLoading(false);
    if ($detail) $detail.style.display = "none";
    if ($error) {
      $error.textContent = message;
      $error.style.display = "block";
    }
  }

  /** 与侧栏 / 选项页一致：请求头携带本地服务密钥（user-config webSocketAuthToken）。 */
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

  /** 本机 loopback：storage 尚无密钥时从引导接口写入（与侧栏首次连接一致）。 */
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

  function showDetail(planId, meta, content) {
    currentPlanId = planId;
    currentPlan = { meta, content };
    showLoading(false);
    if ($error) $error.style.display = "none";
    if ($detail) $detail.style.display = "block";
    if ($planTitle) $planTitle.textContent = meta.title || planId;
    if ($contentView) {
      if (typeof marked !== "undefined")
        $contentView.innerHTML = marked.parse(content || "");
      else
        $contentView.textContent = content || "";
    }
    if ($contentEdit) $contentEdit.value = content || "";
    if ($contentEditWrap) $contentEditWrap.style.display = "none";
    if ($contentView) $contentView.style.display = "block";
    if ($editBtn) $editBtn.style.display = "inline-block";
    if ($saveBtn) $saveBtn.style.display = "none";
    if ($cancelBtn) $cancelBtn.style.display = "none";

    const createdBy = (meta.createdBy || "").toLowerCase();
    const isChrome = typeof chrome !== "undefined" && chrome.storage && chrome.sidePanel;

    if ($executePlanBtn) {
      $executePlanBtn.style.display = "inline-block";
      $executePlanBtn.disabled = false;
    }
    if ($executeHint) {
      $executeHint.style.display = "none";
      $executeHint.textContent = "";
      if (isChrome && createdBy && createdBy !== "chrome") {
        if (createdBy === "office-word") {
          $executeHint.textContent = "该计划在 Word 中创建，请在 Word 任务窗格中执行。";
        } else if (createdBy === "office-excel") {
          $executeHint.textContent = "该计划在 Excel 中创建，请在 Excel 任务窗格中执行。";
        } else if (createdBy === "office-powerpoint") {
          $executeHint.textContent = "该计划在 PowerPoint 中创建，请在 PowerPoint 任务窗格中执行。";
        } else if (createdBy === "wps") {
          $executeHint.textContent = "该计划在 WPS 中创建，请在 WPS 任务窗格中执行。";
        } else {
          $executeHint.textContent = "请在创建该计划的应用（" + (meta.createdBy || "未知") + "）中执行。";
        }
        $executeHint.style.display = "block";
      }
    }
  }

  async function loadPlan(planId) {
    if (!planId) {
      showError("缺少计划 ID，请通过链接打开计划页（如 plans.html?id=xxx）。");
      return;
    }
    showLoading(true);
    try {
      const res = await tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(planId));
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        throw new Error(data.message || (res.status === 404 ? "未找到该计划" : res.statusText));
      }
      const data = await res.json();
      showDetail(planId, data.meta || {}, data.content || "");
    } catch (err) {
      console.error("load plan failed", err);
      showError(err.message || "加载计划失败，请确认后台已启动。");
    }
  }

  if ($editBtn) {
    $editBtn.addEventListener("click", () => {
      if (!$contentEdit || !$contentView || !$contentEditWrap) return;
      $contentEdit.value = currentPlan ? currentPlan.content : "";
      $contentView.style.display = "none";
      $contentEditWrap.style.display = "block";
      $editBtn.style.display = "none";
      $saveBtn.style.display = "inline-block";
      $cancelBtn.style.display = "inline-block";
    });
  }

  if ($cancelBtn) {
    $cancelBtn.addEventListener("click", () => {
      if (!$contentEditWrap || !$contentView) return;
      $contentEditWrap.style.display = "none";
      $contentView.style.display = "block";
      $editBtn.style.display = "inline-block";
      $saveBtn.style.display = "none";
      $cancelBtn.style.display = "none";
    });
  }

  if ($saveBtn && $contentEdit) {
    $saveBtn.addEventListener("click", async () => {
      if (!currentPlanId) return;
      const content = $contentEdit.value;
      try {
        const res = await tasklyFetch(API_BASE + "/api/plans/" + encodeURIComponent(currentPlanId), {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ content })
        });
        if (!res.ok) {
          const data = await res.json().catch(() => ({}));
          throw new Error(data.message || res.statusText);
        }
        if (currentPlan) currentPlan.content = content;
        if ($contentView) {
          if (typeof marked !== "undefined") $contentView.innerHTML = marked.parse(content || "");
          else $contentView.textContent = content || "";
        }
        $contentEditWrap.style.display = "none";
        $contentView.style.display = "block";
        $editBtn.style.display = "inline-block";
        $saveBtn.style.display = "none";
        $cancelBtn.style.display = "none";
      } catch (err) {
        console.error("save plan failed", err);
      }
    });
  }

  if ($usePlanBtn) {
    $usePlanBtn.addEventListener("click", () => {
      if (!currentPlanId || !currentPlan) return;
      const title = currentPlan.meta.title || currentPlanId;
      if (typeof chrome !== "undefined" && chrome.storage?.local) {
        chrome.storage.local.set({ copilot_plan_id: currentPlanId, copilot_plan_title: title }, () => {
          $usePlanBtn.textContent = "已设为当前计划";
          setTimeout(() => { $usePlanBtn.textContent = "作为当前计划"; }, 2000);
        });
      }
    });
  }

  if ($executePlanBtn) {
    $executePlanBtn.addEventListener("click", () => {
      if (!currentPlanId || !currentPlan) return;
      const createdBy = (currentPlan.meta.createdBy || "").toLowerCase();
      const title = currentPlan.meta.title || currentPlanId;

      if (typeof chrome === "undefined" || !chrome.storage?.local) {
        if ($executeHint) {
          $executeHint.textContent = "当前环境不支持执行，请在 Chrome 扩展或 Office/WPS 任务窗格中打开。";
          $executeHint.style.display = "block";
        }
        return;
      }

      if (createdBy === "chrome") {
        chrome.storage.local.set({
          [STORAGE_EXECUTE_PLAN_ID]: currentPlanId,
          [STORAGE_EXECUTE_PLAN_TITLE]: title
        }, () => {
          if (chrome.sidePanel?.open) {
            chrome.sidePanel.open({ windowId: chrome.windows.WINDOW_ID_CURRENT }).catch(() => {});
          }
          $executePlanBtn.textContent = "已请求执行，请到侧边栏查看";
          setTimeout(() => { $executePlanBtn.textContent = "确认并开始执行"; }, 3000);
        });
        return;
      }

      if ($executeHint) {
        if (createdBy === "office-word") {
          $executeHint.textContent = "该计划在 Word 中创建，请在 Word 任务窗格中执行。";
        } else if (createdBy === "office-excel") {
          $executeHint.textContent = "该计划在 Excel 中创建，请在 Excel 任务窗格中执行。";
        } else if (createdBy === "office-powerpoint") {
          $executeHint.textContent = "该计划在 PowerPoint 中创建，请在 PowerPoint 任务窗格中执行。";
        } else if (createdBy === "wps") {
          $executeHint.textContent = "该计划在 WPS 中创建，请在 WPS 任务窗格中执行。";
        } else {
          $executeHint.textContent = "请在创建该计划的应用中执行。";
        }
        $executeHint.style.display = "block";
      }
    });
  }

  if (typeof marked !== "undefined") {
    marked.setOptions({
      highlight: function (code, lang) {
        if (typeof hljs !== "undefined" && lang && hljs.getLanguage(lang))
          return hljs.highlight(code, { language: lang }).value;
        if (typeof hljs !== "undefined") return hljs.highlightAuto(code).value;
        return code;
      },
      breaks: true
    });
  }

  function tasklyRefreshHljsLink() {
    var link = document.getElementById("taskly-hljs-theme");
    if (link && typeof TasklyTheme !== "undefined") {
      link.href = TasklyTheme.getHljsStylesheetHref(document.documentElement.getAttribute("data-theme") || "dark");
    }
  }

  window.addEventListener("storage", function (e) {
    if (e.key !== "tasklyUiTheme") return;
    if (typeof TasklyTheme !== "undefined") {
      TasklyTheme.applyThemeDomOnly(e.newValue != null && e.newValue !== "" ? e.newValue : "dark");
    }
    tasklyRefreshHljsLink();
  });

  tasklyRefreshHljsLink();

  function tasklyEnsurePlansApiBase() {
    if (tasklyPlansApiReady) return tasklyPlansApiReady;
    tasklyPlansApiReady = TasklyLocalService.tasklyResolveLocalServiceBase(
      typeof chrome !== "undefined" && chrome.storage && chrome.storage.local ? chrome.storage.local : null
    )
      .then(function (r) {
        API_BASE = TasklyLocalService.normalizeBase(r.baseUrl);
      })
      .catch(function (err) {
        tasklyPlansApiReady = null;
        throw err;
      });
    return tasklyPlansApiReady;
  }

  const planId = getPlanIdFromUrl();
  tasklyEnsurePlansApiBase()
    .then(function () {
      return ensureLocalServiceTokenFromBootstrap(API_BASE).then(function () {
        tasklyFetch(API_BASE + "/api/config")
          .then(function (res) { return res.ok ? res.json() : null; })
          .then(function (j) {
            if (!j || typeof TasklyTheme === "undefined") return;
            var id = j.uiThemeId || j.UiThemeId;
            if (id) TasklyTheme.setTheme(id);
            tasklyRefreshHljsLink();
          })
          .catch(function () {});
        return loadPlan(planId);
      });
    })
    .catch(function (err) {
      showError(err.message || "无法连接本机 Office Copilot 服务。");
    });
})();
