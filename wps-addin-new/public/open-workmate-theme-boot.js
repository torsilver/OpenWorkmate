/**
 * 首屏同步应用 OpenWorkmate UI 主题（data-theme + CSS 变量）。
 * 存储键 openWorkmateUiTheme；合法值见 OpenWorkmateTheme.allIds。
 */
(function (global) {
  "use strict";
  var KEY = "openWorkmateUiTheme";
  var IDS = { light: 1, dark: 1, blocks: 1, modern: 1, minimal: 1, lines: 1, sketch: 1, parchment: 1 };

  function normalize(t) {
    t = (t == null ? "" : String(t)).trim();
    return IDS[t] ? t : "dark";
  }

  function applyFromStorage() {
    try {
      var t = normalize(global.localStorage.getItem(KEY));
      document.documentElement.setAttribute("data-theme", t);
      return t;
    } catch (e) {
      document.documentElement.setAttribute("data-theme", "dark");
      return "dark";
    }
  }

  function setTheme(t) {
    t = normalize(t);
    try {
      global.localStorage.setItem(KEY, t);
    } catch (e) { /* ignore */ }
    document.documentElement.setAttribute("data-theme", t);
    return t;
  }

  /** 仅设置 DOM，不写 localStorage（用于从后端覆盖后的单次应用） */
  function applyThemeDomOnly(t) {
    t = normalize(t);
    document.documentElement.setAttribute("data-theme", t);
    return t;
  }

  global.OpenWorkmateTheme = {
    KEY: KEY,
    normalize: normalize,
    applyFromStorage: applyFromStorage,
    setTheme: setTheme,
    applyThemeDomOnly: applyThemeDomOnly,
    allIds: Object.keys(IDS),
    isLightUi: function (t) {
      t = normalize(t);
      return t === "light" || t === "minimal" || t === "lines" || t === "sketch" || t === "parchment";
    },
    getMermaidTheme: function (t) {
      return global.OpenWorkmateTheme.isLightUi(t) ? "neutral" : "dark";
    },
    getHljsStylesheetHref: function (t) {
      return global.OpenWorkmateTheme.isLightUi(t) ? "libs/github.min.css" : "libs/github-dark.min.css";
    },
  };

  applyFromStorage();
})(typeof self !== "undefined" ? self : this);
