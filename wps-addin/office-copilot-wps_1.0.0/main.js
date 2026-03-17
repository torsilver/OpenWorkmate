/**
 * Office Copilot WPS 加载项入口与 Ribbon 回调。
 * 由 index.html 加载；ribbon.xml 中的 onAction 会调用此处暴露的函数。
 */
(function () {
  "use strict";

  var ADDON_FOLDER_NAME = "office-copilot-wps_1.0.0";
  var TASKPANE_PAGE = "taskpane.html";

  function getTaskpaneUrl() {
    try {
      var base = null;
      if (typeof wps !== "undefined") {
        if (wps.Env && wps.Env.GetAppDataPath) base = wps.Env.GetAppDataPath();
        if (!base && wps.Application && wps.Application.Env && wps.Application.Env.GetAppDataPath) base = wps.Application.Env.GetAppDataPath();
      }
      if (base) {
        var sep = base.indexOf("\\") >= 0 ? "\\" : "/";
        return base + sep + "kingsoft" + sep + "wps" + sep + "jsaddons" + sep + ADDON_FOLDER_NAME + sep + TASKPANE_PAGE;
      }
    } catch (e) {}
    var loc = typeof location !== "undefined" ? location.href : "";
    if (loc) {
      var last = loc.lastIndexOf("/");
      return (last >= 0 ? loc.slice(0, last + 1) : loc) + TASKPANE_PAGE;
    }
    return TASKPANE_PAGE;
  }

  function OpenTaskPane() {
    try {
      if (typeof wps === "undefined") {
        return;
      }
      var url = getTaskpaneUrl();
      var tskpane = wps.CreateTaskpane(url);
      if (tskpane) {
        tskpane.Visible = true;
      }
    } catch (e) {
      if (typeof console !== "undefined") console.error("OpenTaskPane:", e);
    }
  }

  function OnRibbonLoad() {
    try {
      if (typeof wps !== "undefined" && wps.ribbon && wps.ribbon.LoadRibbon) {
        wps.ribbon.LoadRibbon("");
      }
    } catch (e) {}
  }

  window.OpenTaskPane = OpenTaskPane;
  window.OnRibbonLoad = OnRibbonLoad;
  if (typeof window.ribbon !== "object") window.ribbon = {};
  window.ribbon.OpenTaskPane = OpenTaskPane;
  window.ribbon.OnRibbonLoad = OnRibbonLoad;
})();
