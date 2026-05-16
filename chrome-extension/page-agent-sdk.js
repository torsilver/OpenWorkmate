/**
 * Open Workmate page agent v1 — 对齐 Chrome 规范：仅页内基座，不经 WebSocket 直连后台。
 * 由 sidepanel 通过 chrome.scripting.executeScript(files) 注入。
 */
(function () {
  "use strict";

  var NS = "__OWM_PAGE_AGENT_V1";
  if (globalThis[NS] && globalThis[NS].version === 1) return;

  var refMap = new Map();
  var observeUrl = "";

  function fail(code, message) {
    return { ok: false, error: { code: code, message: message || "" } };
  }

  function ok(op, extra) {
    var o = { ok: true, op: op };
    if (extra && typeof extra === "object") {
      for (var k in extra) {
        if (Object.prototype.hasOwnProperty.call(extra, k)) o[k] = extra[k];
      }
    }
    return o;
  }

  function checkStale() {
    try {
      var href = String(location.href || "");
      if (observeUrl && href !== observeUrl) {
        refMap.clear();
        return fail("STALE_REF", "页面已导航，请重新执行 observe。");
      }
    } catch (e) {
      return fail("BAD_REQUEST", e && e.message ? String(e.message) : String(e));
    }
    return null;
  }

  function isVisible(el) {
    if (!el || !el.getBoundingClientRect) return false;
    var st = window.getComputedStyle(el);
    if (st.display === "none" || st.visibility === "hidden" || st.opacity === "0") return false;
    var r = el.getBoundingClientRect();
    return r.width >= 1 && r.height >= 1;
  }

  function labelFor(el) {
    try {
      var a = el.getAttribute && (el.getAttribute("aria-label") || el.getAttribute("title"));
      if (a && String(a).trim()) return String(a).trim().slice(0, 200);
      var t = (el.innerText || el.textContent || "").replace(/\s+/g, " ").trim();
      if (t) return t.slice(0, 200);
      var ph = el.getAttribute && el.getAttribute("placeholder");
      if (ph) return String(ph).trim().slice(0, 200);
      return "";
    } catch (e) {
      return "";
    }
  }

  function roleFor(el) {
    var r = el.getAttribute && el.getAttribute("role");
    if (r) return String(r);
    var tag = el.tagName ? el.tagName.toLowerCase() : "";
    if (tag === "a") return "link";
    if (tag === "button") return "button";
    if (tag === "input") return "textbox";
    if (tag === "textarea") return "textbox";
    if (tag === "select") return "combobox";
    return tag || "element";
  }

  function collectCandidates(root, max) {
    var set = new Set();
    var sel =
      'button, a[href], input:not([type="hidden"]), textarea, select, [role="button"], [role="link"], [role="textbox"], [role="checkbox"], [role="radio"], [tabindex]:not([tabindex="-1"])';
    var list = [];
    try {
      root.querySelectorAll(sel).forEach(function (el) {
        if (list.length >= max) return;
        if (set.has(el)) return;
        if (!isVisible(el)) return;
        set.add(el);
        list.push(el);
      });
    } catch (e) {}
    return list;
  }

  function opObserve(params) {
    refMap.clear();
    observeUrl = String(location.href || "");
    var maxNodes = Math.min(120, Math.max(5, Number(params && params.maxNodes) || 80));
    var root = document.body || document.documentElement;
    if (!root) return fail("NOT_FOUND", "document.body 不可用。");

    var els = collectCandidates(root, maxNodes);
    var nodes = [];
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      var ref = "r" + i;
      refMap.set(ref, el);
      var tag = el.tagName ? el.tagName.toLowerCase() : "?";
      var inpType = tag === "input" && el.type ? String(el.type).toLowerCase() : "";
      nodes.push({
        ref: ref,
        tag: tag,
        type: inpType || undefined,
        role: roleFor(el),
        name: labelFor(el) || undefined
      });
    }

    return ok("observe", {
      url: observeUrl,
      title: String(document.title || ""),
      refCount: nodes.length,
      nodes: nodes
    });
  }

  function resolveRef(ref) {
    if (!ref || typeof ref !== "string") return { err: fail("BAD_REQUEST", "缺少 ref。") };
    var el = refMap.get(ref.trim());
    if (!el) return { err: fail("NOT_FOUND", "未知 ref，请先 observe 或 ref 已失效。") };
    if (!el.isConnected) {
      refMap.delete(ref.trim());
      return { err: fail("NOT_FOUND", "元素已从文档移除。") };
    }
    return { el: el };
  }

  function opClick(params) {
    var st = checkStale();
    if (st) return st;
    var r = resolveRef(params && params.ref);
    if (r.err) return r.err;
    var el = r.el;
    try {
      el.scrollIntoView({ block: "nearest", inline: "nearest" });
      if (typeof el.click === "function") el.click();
      else return fail("BAD_REQUEST", "该元素不支持 click()。");
      return ok("click", { ref: String(params.ref).trim() });
    } catch (e) {
      return fail("BAD_REQUEST", e && e.message ? String(e.message) : String(e));
    }
  }

  function opFill(params) {
    var st = checkStale();
    if (st) return st;
    var r = resolveRef(params && params.ref);
    if (r.err) return r.err;
    var el = r.el;
    var tag = el.tagName ? el.tagName.toUpperCase() : "";
    if (tag !== "INPUT" && tag !== "TEXTAREA") {
      return fail("BAD_REQUEST", "fill 仅支持 input/textarea。");
    }
    var val = params.value != null ? String(params.value) : "";
    try {
      el.focus();
      el.value = val;
      el.dispatchEvent(new Event("input", { bubbles: true }));
      el.dispatchEvent(new Event("change", { bubbles: true }));
      return ok("fill", { ref: String(params.ref).trim() });
    } catch (e) {
      return fail("BAD_REQUEST", e && e.message ? String(e.message) : String(e));
    }
  }

  function opScrollIntoView(params) {
    var st = checkStale();
    if (st) return st;
    var r = resolveRef(params && params.ref);
    if (r.err) return r.err;
    try {
      r.el.scrollIntoView({ block: "nearest", inline: "nearest" });
      return ok("scrollIntoView", { ref: String(params.ref).trim() });
    } catch (e) {
      return fail("BAD_REQUEST", e && e.message ? String(e.message) : String(e));
    }
  }

  function sleep(ms) {
    return new Promise(function (res) {
      setTimeout(res, ms);
    });
  }

  async function opWaitFor(params) {
    var st = checkStale();
    if (st) return st;
    var ref = params && params.ref;
    var first = resolveRef(ref);
    if (first.err) return first.err;
    var timeoutMs = Math.min(120000, Math.max(100, Number(params && params.timeoutMs) || 10000));
    var t0 = Date.now();
    while (Date.now() - t0 < timeoutMs) {
      var st2 = checkStale();
      if (st2) return st2;
      var r = resolveRef(ref);
      if (r.err) return r.err;
      if (r.el && isVisible(r.el)) {
        return ok("waitFor", { ref: String(ref).trim() });
      }
      await sleep(100);
    }
    return fail("TIMEOUT", "在 " + timeoutMs + "ms 内未等到 ref 可见。");
  }

  async function dispatch(req) {
    if (!req || typeof req !== "object") return fail("BAD_REQUEST", "请求体必须是对象。");
    var op = String(req.op || "").trim().toLowerCase();
    if (!op) return fail("BAD_REQUEST", "缺少 op。");

    if (op === "observe") return opObserve(req);
    if (op === "waitfor") return await opWaitFor(req);
    if (op === "click") return opClick(req);
    if (op === "fill") return opFill(req);
    if (op === "scrollintoview") return opScrollIntoView(req);

    return fail("BAD_REQUEST", "未知 op: " + op);
  }

  globalThis[NS] = {
    version: 1,
    dispatch: dispatch
  };
})();
