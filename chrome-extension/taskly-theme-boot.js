/**
 * 首屏同步应用 Taskly UI 主题（data-theme + CSS 变量）。
 * 存储键 tasklyUiTheme；合法值见 TasklyTheme.allIds。
 */
(function (global) {
  "use strict";
  var KEY = "tasklyUiTheme";
  var IDS = { light: 1, dark: 1, blocks: 1, modern: 1, minimal: 1, lines: 1, sketch: 1 };

  function normalize(t) {
    t = (t == null ? "" : String(t)).trim();
    return IDS[t] ? t : "dark";
  }

  /** 与 #taskly-hljs-theme 同步（扩展 CSP 禁止内联 script，逻辑由原 head 内联块迁入） */
  function syncHljsStylesheetLink() {
    try {
      if (!global.TasklyTheme) return;
      var el = document.getElementById("taskly-hljs-theme");
      if (!el) return;
      var t = document.documentElement.getAttribute("data-theme") || "dark";
      el.href = global.TasklyTheme.getHljsStylesheetHref(t);
    } catch (e) { /* ignore */ }
  }

  function applyFromStorage() {
    try {
      var t = normalize(global.localStorage.getItem(KEY));
      document.documentElement.setAttribute("data-theme", t);
      syncHljsStylesheetLink();
      return t;
    } catch (e) {
      document.documentElement.setAttribute("data-theme", "dark");
      syncHljsStylesheetLink();
      return "dark";
    }
  }

  function setTheme(t) {
    t = normalize(t);
    try {
      global.localStorage.setItem(KEY, t);
    } catch (e) { /* ignore */ }
    document.documentElement.setAttribute("data-theme", t);
    syncHljsStylesheetLink();
    return t;
  }

  /** 仅设置 DOM，不写 localStorage（用于从后端覆盖后的单次应用） */
  function applyThemeDomOnly(t) {
    t = normalize(t);
    document.documentElement.setAttribute("data-theme", t);
    syncHljsStylesheetLink();
    return t;
  }

  global.TasklyTheme = {
    KEY: KEY,
    normalize: normalize,
    applyFromStorage: applyFromStorage,
    setTheme: setTheme,
    applyThemeDomOnly: applyThemeDomOnly,
    allIds: Object.keys(IDS),
    isLightUi: function (t) {
      t = normalize(t);
      return t === "light" || t === "minimal" || t === "lines" || t === "sketch";
    },
    getMermaidTheme: function (t) {
      return global.TasklyTheme.isLightUi(t) ? "neutral" : "dark";
    },
    getHljsStylesheetHref: function (t) {
      return global.TasklyTheme.isLightUi(t) ? "libs/github.min.css" : "libs/github-dark.min.css";
    },
  };

  applyFromStorage();
  syncHljsStylesheetLink();
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", syncHljsStylesheetLink);
  } else {
    syncHljsStylesheetLink();
  }
})(typeof self !== "undefined" ? self : this);
