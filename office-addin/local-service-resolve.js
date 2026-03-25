/**
 * 本机 Office Copilot 服务地址解析：浏览器扩展无法读本地 JSON，故先尝试 chrome.storage 缓存，
 * 再按 127.0.0.1 端口扫描；服务端在 bootstrap 中返回 localServicePortScanStart/Count 供与配置对齐。
 */
(function (global) {
  var TASKLY_DEFAULT_PORT_START = 8765;
  var TASKLY_PORT_TRY_COUNT = 10;
  var TASKLY_BASE_URL_STORAGE_KEY = "tasklyLocalServiceBaseUrl";

  function normalizeBase(url) {
    if (!url) return "";
    return String(url).replace(/\/$/, "");
  }

  function fetchWithTimeout(url, ms) {
    var ctrl = new AbortController();
    var id = setTimeout(function () {
      try {
        ctrl.abort();
      } catch (e) { /* ignore */ }
    }, ms || 1500);
    return fetch(url, { method: "GET", signal: ctrl.signal })
      .then(function (r) {
        clearTimeout(id);
        return r;
      })
      .catch(function (e) {
        clearTimeout(id);
        throw e;
      });
  }

  function fetchBootstrapJson(base) {
    return fetchWithTimeout(normalizeBase(base) + "/api/bootstrap/local-service-auth", 1500).then(function (r) {
      return r.ok ? r.json() : null;
    });
  }

  function readSessionStoredBase() {
    try {
      if (typeof sessionStorage === "undefined") return null;
      return normalizeBase(sessionStorage.getItem(TASKLY_BASE_URL_STORAGE_KEY));
    } catch (e) {
      return null;
    }
  }

  function writeSessionStoredBase(base) {
    try {
      if (typeof sessionStorage !== "undefined" && base)
        sessionStorage.setItem(TASKLY_BASE_URL_STORAGE_KEY, normalizeBase(base));
    } catch (e) { /* ignore */ }
  }

  /**
   * @param {chrome.storage.StorageArea | null} storage chrome.storage.local 或 null（非扩展环境）
   * @returns {Promise<{ baseUrl: string, bootstrap: object }>}
   */
  function tasklyResolveLocalServiceBase(storage) {
    function persistCanonical(canonical, useChromeStorage) {
      if (useChromeStorage && storage && storage.set) {
        var o = {};
        o[TASKLY_BASE_URL_STORAGE_KEY] = canonical;
        storage.set(o, function () {});
      } else writeSessionStoredBase(canonical);
    }

    function tryStoredThenScan(storedBase, useChromeStorage) {
      if (storedBase) {
        return fetchBootstrapJson(storedBase).then(function (j) {
          if (j && j.ok) {
            var canonical = j.localServiceBaseUrl ? normalizeBase(j.localServiceBaseUrl) : storedBase;
            persistCanonical(canonical, useChromeStorage);
            return { baseUrl: canonical, bootstrap: j };
          }
          return scanPorts(useChromeStorage, TASKLY_DEFAULT_PORT_START, TASKLY_PORT_TRY_COUNT);
        });
      }
      return scanPorts(useChromeStorage, TASKLY_DEFAULT_PORT_START, TASKLY_PORT_TRY_COUNT);
    }

    function scanPorts(useChromeStorage, start, count) {
      function attempt(i) {
        if (i >= count) {
          return Promise.reject(
            new Error(
              "找不到本机 Office Copilot 服务（已扫描 127.0.0.1:" +
                start +
                "–" +
                (start + count - 1) +
                "）。请确认后台已启动。"
            )
          );
        }
        var port = start + i;
        var base = "http://127.0.0.1:" + port;
        return fetchBootstrapJson(base).then(function (j) {
          if (j && j.ok) {
            var canonical = j.localServiceBaseUrl ? normalizeBase(j.localServiceBaseUrl) : base;
            persistCanonical(canonical, useChromeStorage);
            return { baseUrl: canonical, bootstrap: j };
          }
          return attempt(i + 1);
        });
      }
      return attempt(0);
    }

    if (storage && typeof storage.get === "function") {
      return new Promise(function (resolve, reject) {
        try {
          storage.get([TASKLY_BASE_URL_STORAGE_KEY], function (r) {
            if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.lastError) {
              tryStoredThenScan(readSessionStoredBase(), false).then(resolve, reject);
              return;
            }
            var stored = r && r[TASKLY_BASE_URL_STORAGE_KEY] ? normalizeBase(r[TASKLY_BASE_URL_STORAGE_KEY]) : null;
            tryStoredThenScan(stored, true).then(resolve, reject);
          });
        } catch (e) {
          tryStoredThenScan(readSessionStoredBase(), false).then(resolve, reject);
        }
      });
    }

    return tryStoredThenScan(readSessionStoredBase(), false);
  }

  /** @returns {{ apiBase: string, wsUrl: string }} */
  function tasklyHttpWsFromBase(baseUrl) {
    var b = normalizeBase(baseUrl);
    if (!b) return { apiBase: "", wsUrl: "" };
    try {
      var u = new URL(b + "/");
      var wsProto = u.protocol === "https:" ? "wss:" : "ws:";
      return { apiBase: b, wsUrl: wsProto + "//" + u.host + "/ws" };
    } catch (e) {
      var hostPart = b.replace(/^https?:\/\//, "").split("/")[0];
      return { apiBase: b, wsUrl: "ws://" + hostPart + "/ws" };
    }
  }

  global.TasklyLocalService = {
    TASKLY_DEFAULT_PORT_START: TASKLY_DEFAULT_PORT_START,
    TASKLY_PORT_TRY_COUNT: TASKLY_PORT_TRY_COUNT,
    TASKLY_BASE_URL_STORAGE_KEY: TASKLY_BASE_URL_STORAGE_KEY,
    normalizeBase: normalizeBase,
    tasklyResolveLocalServiceBase: tasklyResolveLocalServiceBase,
    tasklyHttpWsFromBase: tasklyHttpWsFromBase
  };
})(typeof self !== "undefined" ? self : this);
