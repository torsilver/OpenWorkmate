const TASKLY_DEFAULT_PORT_START = 8765
const TASKLY_PORT_TRY_COUNT = 10
const TASKLY_BASE_URL_STORAGE_KEY = 'tasklyLocalServiceBaseUrl'

export function normalizeBase(url) {
  if (!url) return ''
  return String(url).replace(/\/$/, '')
}

function fetchWithTimeout(url, ms) {
  const ctrl = new AbortController()
  const id = setTimeout(() => {
    try {
      ctrl.abort()
    } catch {
      /* ignore */
    }
  }, ms || 1500)
  return fetch(url, { method: 'GET', signal: ctrl.signal })
    .then((r) => {
      clearTimeout(id)
      return r
    })
    .catch((e) => {
      clearTimeout(id)
      throw e
    })
}

function fetchBootstrapJson(base) {
  return fetchWithTimeout(normalizeBase(base) + '/api/bootstrap/local-service-auth', 1500).then((r) =>
    r.ok ? r.json() : null
  )
}

function readSessionStoredBase() {
  try {
    if (typeof sessionStorage === 'undefined') return null
    return normalizeBase(sessionStorage.getItem(TASKLY_BASE_URL_STORAGE_KEY))
  } catch {
    return null
  }
}

function writeSessionStoredBase(base) {
  try {
    if (typeof sessionStorage !== 'undefined' && base)
      sessionStorage.setItem(TASKLY_BASE_URL_STORAGE_KEY, normalizeBase(base))
  } catch {
    /* ignore */
  }
}

function scanPorts(start, count, persist) {
  function attempt(i) {
    if (i >= count) {
      return Promise.reject(
        new Error(
          `找不到本机 Office Copilot 服务（已扫描 127.0.0.1:${start}–${start + count - 1}）。请确认后台已启动。`
        )
      )
    }
    const port = start + i
    const base = 'http://127.0.0.1:' + port
    return fetchBootstrapJson(base).then((j) => {
      if (j && j.ok) {
        const canonical = j.localServiceBaseUrl ? normalizeBase(j.localServiceBaseUrl) : base
        persist(canonical)
        return { baseUrl: canonical, bootstrap: j }
      }
      return attempt(i + 1)
    })
  }
  return attempt(0)
}

export function tasklyResolveLocalServiceBase() {
  const persist = (canonical) => writeSessionStoredBase(canonical)
  const stored = readSessionStoredBase()
  if (stored) {
    return fetchBootstrapJson(stored).then((j) => {
      if (j && j.ok) {
        const canonical = j.localServiceBaseUrl ? normalizeBase(j.localServiceBaseUrl) : stored
        persist(canonical)
        return { baseUrl: canonical, bootstrap: j }
      }
      return scanPorts(TASKLY_DEFAULT_PORT_START, TASKLY_PORT_TRY_COUNT, persist)
    })
  }
  return scanPorts(TASKLY_DEFAULT_PORT_START, TASKLY_PORT_TRY_COUNT, persist)
}

export function tasklyHttpWsFromBase(baseUrl) {
  const b = normalizeBase(baseUrl)
  if (!b) return { apiBase: '', wsUrl: '' }
  try {
    const u = new URL(b + '/')
    const wsProto = u.protocol === 'https:' ? 'wss:' : 'ws:'
    return { apiBase: b, wsUrl: `${wsProto}//${u.host}/ws` }
  } catch {
    const hostPart = b.replace(/^https?:\/\//, '').split('/')[0]
    return { apiBase: b, wsUrl: `ws://${hostPart}/ws` }
  }
}
