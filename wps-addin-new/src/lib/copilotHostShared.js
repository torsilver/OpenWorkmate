/**
 * 与 Chrome / Office 任务窗格共用的宿主无关协议片段（ESM）。
 * Office 侧镜像见 office-addin/copilot-host-shared.js（IIFE），修改时请对齐两端。
 */

const DEFAULT_TELEMETRY_TIER = 'full'
const DEFAULT_TELEMETRY_INGEST_LOG_LEVEL = 'information'

/** 与 Chrome sidepanel：tool_invocation_end 兜底判定 */
/**
 * 将文本里 JSON 常见的字面量 `\uXXXX`（可多层转义）还原为 Unicode 字符，便于时间线展示工具参数/结果。
 * 仅做安全子集替换，不解析整段 JSON。
 * 注意：流式 `tool_call_delta` 时不要每包对整段调用（易卡顿、看起来像整块出现）；在草稿折叠/结束处对整段调用一次即可。
 * @param {string} s
 * @returns {string}
 */
export function decodeJsonStyleUnicodeEscapes(s) {
  if (s == null || typeof s !== 'string') return s
  if (s.indexOf('\\u') === -1) return s
  let t = s
  let prev = ''
  let guard = 0
  while (t !== prev && t.indexOf('\\u') !== -1 && guard < 24) {
    prev = t
    t = t.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    guard++
  }
  return t
}

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

/**
 * 用于「上下文占用」圆环：`prompt_tokens` 相对固定预算的比例（上游不返回 max context 时的务实默认）。
 * 与 Chrome sidepanel / Office taskpane 中同名常量保持一致。
 */
export const DEFAULT_CONTEXT_TOKEN_BUDGET = 131072

/**
 * 解析 WS <code>stream_usage</code> 的 <code>content</code>（通常为 usage 对象的 JSON 字符串）。
 * @returns {{ promptTokens: number|null, completionTokens: number|null, totalTokens: number|null }|null}
 */
export function parseStreamUsagePayload(content) {
  try {
    const raw = typeof content === 'string' ? content : String(content || '')
    if (!raw.trim()) return null
    const u = JSON.parse(raw)
    const prompt =
      typeof u.prompt_tokens === 'number'
        ? u.prompt_tokens
        : typeof u.PromptTokens === 'number'
          ? u.PromptTokens
          : null
    const completion =
      typeof u.completion_tokens === 'number'
        ? u.completion_tokens
        : typeof u.CompletionTokens === 'number'
          ? u.CompletionTokens
          : null
    const total =
      typeof u.total_tokens === 'number'
        ? u.total_tokens
        : typeof u.TotalTokens === 'number'
          ? u.TotalTokens
          : null
    return { promptTokens: prompt, completionTokens: completion, totalTokens: total }
  } catch {
    return null
  }
}

/**
 * @param {ReturnType<typeof parseStreamUsagePayload>} parsed
 * @param {number} [budget]
 * @returns {number|null} 0..1
 */
export function usagePromptFillRatio(parsed, budget = DEFAULT_CONTEXT_TOKEN_BUDGET) {
  if (!parsed || parsed.promptTokens == null || parsed.promptTokens < 0 || !budget || budget <= 0) return null
  return Math.min(1, parsed.promptTokens / budget)
}

/**
 * @param {ReturnType<typeof parseStreamUsagePayload>} parsed
 * @param {number} [budget]
 */
export function buildStreamUsageRingTitle(parsed, budget = DEFAULT_CONTEXT_TOKEN_BUDGET) {
  if (!parsed) return ''
  const parts = []
  if (parsed.promptTokens != null) parts.push(`输入 tokens: ${parsed.promptTokens}`)
  if (parsed.completionTokens != null) parts.push(`输出 tokens: ${parsed.completionTokens}`)
  if (parsed.totalTokens != null) parts.push(`合计: ${parsed.totalTokens}`)
  parts.push(`圆环：输入 / 参考预算 ${budget}（模型真实上限以服务商为准）`)
  return parts.join('\n')
}
