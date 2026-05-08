(function () {
  const COPILOT_TOKEN_STORAGE_KEY = "localServiceAuthToken";
  const POLL_MS = 2000;

  var API_BASE = "http://127.0.0.1:8765";
  var openWorkmateMeetingLiveApiReady = null;

  function openWorkmateEnsureApiBase() {
    if (openWorkmateMeetingLiveApiReady) return openWorkmateMeetingLiveApiReady;
    openWorkmateMeetingLiveApiReady = OpenWorkmateLocalService.openWorkmateResolveLocalServiceBase(
      typeof chrome !== "undefined" && chrome.storage && chrome.storage.local ? chrome.storage.local : null
    )
      .then(function (r) {
        API_BASE = OpenWorkmateLocalService.normalizeBase(r.baseUrl);
      })
      .catch(function (err) {
        openWorkmateMeetingLiveApiReady = null;
        throw err;
      });
    return openWorkmateMeetingLiveApiReady;
  }

  function openWorkmateFetch(url, init) {
    init = init ? Object.assign({}, init) : {};
    return new Promise(function (resolve) {
      chrome.storage.local.get([COPILOT_TOKEN_STORAGE_KEY], function (r) {
        var t = (r && r[COPILOT_TOKEN_STORAGE_KEY] || "").trim();
        var headers = Object.assign({}, init.headers || {});
        if (t) headers["X-OpenWorkmate-Token"] = t;
        init.headers = headers;
        resolve(fetch(url, init));
      });
    });
  }

  function ensureLocalServiceTokenFromBootstrap() {
    return openWorkmateEnsureApiBase().then(function () {
      return fetch(API_BASE + "/api/bootstrap/local-service-auth")
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
    });
  }

  var params = new URLSearchParams(window.location.search || "");
  var sessionId = (params.get("sessionId") || "").trim();
  var $session = document.getElementById("ml-session");
  var $status = document.getElementById("ml-status");
  var $segments = document.getElementById("ml-segments");
  var $summaryBtn = document.getElementById("ml-summary-btn");
  var $err = null;

  function setStatus(t) {
    if ($status) $status.textContent = t || "";
  }

  function showError(msg) {
    if (!$err) {
      $err = document.createElement("p");
      $err.className = "ml-error";
      document.body.insertBefore($err, $segments);
    }
    $err.textContent = msg;
  }

  function clearError() {
    if ($err) {
      $err.textContent = "";
    }
  }

  if (!sessionId) {
    showError("缺少 sessionId 参数。请从侧栏开始「会议监听」以自动打开本页。");
    if ($summaryBtn) $summaryBtn.disabled = true;
    return;
  }

  if ($session) {
    $session.textContent = "sessionId: " + sessionId;
  }

  var afterSeq = -1;
  var seenSeq = Object.create(null);
  var pollTimer = null;

  function appendSegment(seq, text) {
    var wrap = document.createElement("div");
    wrap.className = "ml-seg";
    var meta = document.createElement("div");
    meta.className = "ml-seg-meta";
    meta.textContent = "#" + (seq + 1) + " · sequence " + seq;
    var p = document.createElement("p");
    p.className = "ml-seg-text";
    p.textContent = text;
    wrap.appendChild(meta);
    wrap.appendChild(p);
    $segments.appendChild(wrap);
    window.scrollTo(0, document.body.scrollHeight);
  }

  async function pollOnce() {
    await openWorkmateEnsureApiBase();
    var url = API_BASE + "/api/meeting-transcript/" + encodeURIComponent(sessionId) + "/segments?afterSeq=" + afterSeq;
    try {
      var res = await openWorkmateFetch(url);
      var data = await res.json().catch(function () { return {}; });
      if (!res.ok || data.ok === false) {
        showError((data && data.message) ? data.message : ("拉取实录失败 HTTP " + res.status));
        return;
      }
      clearError();
      var list = data.segments || [];
      var maxLocal = afterSeq;
      for (var i = 0; i < list.length; i++) {
        var row = list[i];
        var s = row.sequence != null ? Number(row.sequence) : -1;
        var tx = row.text != null ? String(row.text) : "";
        if (s >= 0 && tx) {
          if (!seenSeq[s]) {
            seenSeq[s] = true;
            appendSegment(s, tx);
          }
          if (s > maxLocal) maxLocal = s;
        }
      }
      afterSeq = maxLocal;
      setStatus(list.length ? ("已更新 " + list.length + " 段") : "轮询中…");
    } catch (e) {
      showError(e && e.message ? e.message : String(e));
    }
  }

  function startPolling() {
    if (pollTimer) clearInterval(pollTimer);
    pollTimer = setInterval(function () { void pollOnce(); }, POLL_MS);
    void pollOnce();
  }

  if ($summaryBtn) {
    $summaryBtn.addEventListener("click", function () {
      chrome.windows.getCurrent(function (win) {
        var wid = win && win.id != null ? win.id : undefined;
        chrome.runtime.sendMessage(
          { type: "REQUEST_MEETING_SUMMARY", sessionId: sessionId, windowId: wid },
          function (resp) {
            if (chrome.runtime.lastError) {
              showError(chrome.runtime.lastError.message || "无法联系扩展后台");
              return;
            }
            if (!resp || !resp.ok) {
              showError((resp && resp.error) || "总结请求失败");
              return;
            }
            clearError();
            setStatus("已请求 AI 总结，请查看侧栏对话。");
          }
        );
      });
    });
  }

  ensureLocalServiceTokenFromBootstrap()
    .then(function () { return openWorkmateEnsureApiBase(); })
    .then(function () {
      setStatus("连接本机 API…");
      startPolling();
    })
    .catch(function (e) {
      showError("本机服务未就绪，将自动重试：" + (e && e.message ? e.message : String(e)));
      setStatus("等待本机服务…");
      // 仍启动轮询：openWorkmateEnsureApiBase 在 reject 时会清空缓存，后续 pollOnce 会重新扫端口
      startPolling();
    });
})();
