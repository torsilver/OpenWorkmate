/**
 * 与 Chrome / Office 任务窗格共用的宿主无关协议片段（ESM）。
 * Office 侧镜像见 office-addin/copilot-host-shared.js（IIFE），修改时请对齐两端。
 */

const DEFAULT_TELEMETRY_TIER = 'full'
const DEFAULT_TELEMETRY_INGEST_LOG_LEVEL = 'information'

/** 与 Chrome sidepanel：tool_invocation_end 兜底判定 */
export function toolInvocationContentLooksLikeError(c) {
  if (!c) return false
  return (
    c.startsWith('[错误]') ||
    c.startsWith('[保存失败]') ||
    c.startsWith('[记忆未启用]') ||
    c.startsWith('[无效]') ||
    c.startsWith('[MCP Error]') ||
    c.startsWith('[MCP Client Exception]') ||
    c.startsWith('[系统拦截]') ||
    c.startsWith('[检索失败]') ||
    c.startsWith('[创建失败]') ||
    c.startsWith('[更新失败]') ||
    c.startsWith('[生成计划失败]') ||
    c.startsWith('[执行步骤失败]') ||
    c.startsWith('[工具调用失败]') ||
    c.startsWith('[参数绑定失败]') ||
    c.startsWith('Error: Function failed.') ||
    c.startsWith('Error: Requested function') ||
    c.startsWith('Error: Unknown error.')
  )
}

/**
 * @param {object} o
 * @param {string} o.sessionId
 * @param {string} o.clientType
 * @param {string} o.agentProfileId
 * @param {string} [o.token]
 * @param {object|null} [o.bootstrap]
 * @param {() => Storage|null} [o.getStorage]
 */
export function buildWebSocketQueryString(o) {
  o = o || {}
  const getStorage =
    o.getStorage ||
    (() => {
      try {
        return typeof localStorage !== 'undefined' ? localStorage : null
      } catch {
        return null
      }
    })
  const st = getStorage()
  function get(key) {
    try {
      return st ? String(st.getItem(key) || '').trim() : ''
    } catch {
      return ''
    }
  }
  function set(key, val) {
    try {
      if (st) st.setItem(key, val)
    } catch {
      /* ignore */
    }
  }

  const deviceKey = o.telemetryDeviceIdKey || 'tasklyTelemetryDeviceId'
  const emissionKey = o.telemetryClientEmissionKey || 'tasklyTelemetryClientEmission'
  const relayActiveKey = o.telemetryRelayActiveProfileKey || 'tasklyTelemetryRelayActiveProfileId'
  const kindsByProfileKey = o.telemetryEventKindsByProfileKey || 'tasklyTelemetryEventKindsByProfile'

  let devId = get(deviceKey)
  if (!devId) {
    devId = typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : 'tid-' + String(Date.now())
    set(deviceKey, devId)
  }

  const tier = DEFAULT_TELEMETRY_TIER
  const ingestLv = DEFAULT_TELEMETRY_INGEST_LOG_LEVEL
  let clientEmission = get(emissionKey) || 'on'
  if (!clientEmission) clientEmission = 'on'

  let serverAllowsObs = true
  const bootstrap = o.bootstrap
  if (bootstrap && typeof bootstrap.telemetryUserObservabilityEnabled === 'boolean') {
    serverAllowsObs = bootstrap.telemetryUserObservabilityEnabled
  } else {
    serverAllowsObs = clientEmission !== 'off'
  }

  const qs = new URLSearchParams()
  qs.set('sessionId', o.sessionId || '')
  qs.set('clientType', o.clientType || '')
  qs.set('agentProfileId', (o.agentProfileId && String(o.agentProfileId).trim()) || 'default')
  if (o.token) qs.set('token', String(o.token))

  if (serverAllowsObs && tier !== 'off') {
    qs.set('deviceId', devId)
    qs.set('telemetryTier', tier)
    qs.set('telemetryIngestLogLevel', ingestLv)
    let relayActive = get(relayActiveKey) || 'default'
    if (!relayActive) relayActive = 'default'
    let kindsMap = {}
    try {
      const raw = st ? st.getItem(kindsByProfileKey) : ''
      if (raw) kindsMap = JSON.parse(raw)
    } catch {
      kindsMap = {}
    }
    const eventKinds = kindsMap && kindsMap[relayActive]
    if (Array.isArray(eventKinds) && eventKinds.length > 0) {
      qs.set('telemetryEventKinds', eventKinds.join(','))
    }
  }

  return '?' + qs.toString()
}
