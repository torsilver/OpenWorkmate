/**
 * Office Copilot 任务窗格逻辑：WebSocket、消息列表、计划面板、RPC、HITL。
 */
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { marked } from 'marked'
import hljs from 'highlight.js'
import mermaid from 'mermaid'
import { tasklyResolveLocalServiceBase, tasklyHttpWsFromBase } from '../utils/tasklyLocalService.js'
import { getWpsHostKind, assertWpsHost } from '../wps-rpc/hostKind.js'
import { runWpsExcelRpc } from '../wps-rpc/excelRpc.js'
import { wordInsertTableWps } from '../wps-rpc/wordTableRpc.js'
import { wordInsertTextWps } from '../wps-rpc/wordInsertTextRpc.js'
import {
  decodeJsonStyleUnicodeEscapes,
  toolInvocationContentLooksLikeError,
  buildWebSocketQueryString,
  parseStreamUsagePayload,
  usagePromptFillRatio,
  buildStreamUsageRingTitle
} from '../lib/copilotHostShared.js'

let WS_URL = 'ws://127.0.0.1:8765/ws'
let API_BASE = 'http://127.0.0.1:8765'
let apiBaseReadyPromise = null

function ensureApiBase() {
  if (!apiBaseReadyPromise) {
    apiBaseReadyPromise = tasklyResolveLocalServiceBase().then((r) => {
      const hw = tasklyHttpWsFromBase(r.baseUrl)
      API_BASE = hw.apiBase
      WS_URL = hw.wsUrl
    })
  }
  return apiBaseReadyPromise
}
/** 与后端有效密钥一致；通常首次连接会自动从本机引导接口同步，也可手动 localStorage.setItem */
const TASKLY_AUTH_TOKEN_KEY = 'tasklyLocalServiceAuthToken'
/** 对齐 Chrome sidepanel：WebSocket 查询参数 deviceId / telemetryTier / telemetryIngestLogLevel / telemetryEventKinds（localStorage） */
const TASKLY_TELEMETRY_DEVICE_ID_KEY = 'tasklyTelemetryDeviceId'
const TASKLY_TELEMETRY_RELAY_ACTIVE_PROFILE_KEY = 'tasklyTelemetryRelayActiveProfileId'
const TASKLY_TELEMETRY_EVENT_KINDS_BY_PROFILE_KEY = 'tasklyTelemetryEventKindsByProfile'
/** 对齐 Chrome <code>telemetryClientEmission</code>：<code>on</code> | <code>off</code> */
const TASKLY_TELEMETRY_CLIENT_EMISSION_KEY = 'tasklyTelemetryClientEmission'

function getStoredAuthToken() {
  try {
    return (localStorage.getItem(TASKLY_AUTH_TOKEN_KEY) || '').trim()
  } catch {
    return ''
  }
}

function tasklyFetch(url, init = {}) {
  const headers = new Headers(init.headers)
  const t = getStoredAuthToken()
  if (t) headers.set('X-OfficeCopilot-Token', t)
  return fetch(url, { ...init, headers })
}

/** WPS 内 window.open 常被拦截；尽量把链接写入剪贴板，避免用户手打 chrome-extension URL。 */
async function copyTextToClipboard(text) {
  const s = String(text || '')
  if (!s) return false
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(s)
      return true
    }
  } catch {
    /* fall through */
  }
  try {
    const ta = document.createElement('textarea')
    ta.value = s
    ta.setAttribute('readonly', '')
    ta.style.position = 'fixed'
    ta.style.left = '-9999px'
    ta.style.top = '0'
    document.body.appendChild(ta)
    ta.focus()
    ta.select()
    ta.setSelectionRange(0, s.length)
    const ok = document.execCommand('copy')
    document.body.removeChild(ta)
    return !!ok
  } catch {
    return false
  }
}

/** 本机 loopback：同步密钥与 <code>telemetryUserObservabilityEnabled</code>（与 user-config 一致） */
function ensureBootstrapAuthToken() {
  return ensureApiBase().then(() =>
    fetch(`${API_BASE}/api/bootstrap/local-service-auth`)
    .then((r) => (r.ok ? r.json() : null))
    .then((j) => {
      if (!j || !j.ok) return j
      try {
        localStorage.setItem(
          TASKLY_TELEMETRY_CLIENT_EMISSION_KEY,
          j.telemetryUserObservabilityEnabled === false ? 'off' : 'on'
        )
      } catch {
        /* ignore */
      }
      const t = (j.webSocketAuthToken || '').trim()
      if (!t || getStoredAuthToken()) return j
      try {
        localStorage.setItem(TASKLY_AUTH_TOKEN_KEY, t)
      } catch {
        /* ignore */
      }
      return j
    })
    .catch(() => null)
  )
}
const RECONNECT_BASE_MS = 1000
const RECONNECT_MAX_MS = 16000
const CLIENT_TYPE = 'wps'
/** 与 chrome-extension 侧栏一致语义：当前 Agent 配置 id（localStorage）。 */
const STORAGE_ACTIVE_AGENT_PROFILE_ID = 'activeAgentProfileId'
const TIMELINE_TAIL_MAX = 100
const STORAGE_PLAN_STEP_INDEX = 'copilot_plan_step_index'

marked.setOptions({
  highlight(code, lang) {
    if (lang && hljs.getLanguage(lang)) {
      return hljs.highlight(code, { language: lang }).value
    }
    return hljs.highlightAuto(code).value
  },
  breaks: true
})

function tasklyMermaidTheme() {
  if (typeof document === 'undefined') return 'dark'
  const t = document.documentElement.getAttribute('data-theme') || 'dark'
  return t === 'light' || t === 'minimal' || t === 'lines' || t === 'sketch' ? 'neutral' : 'dark'
}

function refreshMermaidConfig() {
  try {
    mermaid.initialize({ startOnLoad: false, theme: tasklyMermaidTheme() })
  } catch {
    /* ignore */
  }
}

/** 对齐 chrome-extension/sidepanel.js：hljs 外链 + Mermaid theme */
function tasklyRefreshEmbedThemes() {
  if (typeof document === 'undefined') return
  const t = document.documentElement.getAttribute('data-theme') || 'dark'
  const link = document.getElementById('taskly-hljs-theme')
  if (link && typeof window !== 'undefined' && typeof window.TasklyTheme !== 'undefined') {
    const rel = window.TasklyTheme.getHljsStylesheetHref(t)
    try {
      link.href = new URL(rel, window.location.href).href
    } catch {
      link.href = rel
    }
  }
  refreshMermaidConfig()
}

tasklyRefreshEmbedThemes()

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (e) => {
    if (e.key !== 'tasklyUiTheme') return
    if (typeof window.TasklyTheme === 'undefined') return
    const v = e.newValue != null && e.newValue !== '' ? e.newValue : 'dark'
    try {
      window.TasklyTheme.applyThemeDomOnly(v)
      tasklyRefreshEmbedThemes()
    } catch {
      /* ignore */
    }
  })
}

let mermaidRunScheduled = false
function scheduleMermaidRun() {
  if (typeof document === 'undefined') return
  if (mermaidRunScheduled) return
  mermaidRunScheduled = true
  nextTick(() => {
    mermaidRunScheduled = false
    refreshMermaidConfig()
    const root = document.querySelector('.copilot-app')
    if (!root || typeof mermaid.render !== 'function') return
    root.querySelectorAll('pre code.language-mermaid').forEach((code, index) => {
      const pre = code.parentElement
      if (!pre || pre.closest('.mermaid-container')) return
      const graph = (code.textContent || '').trim()
      if (!graph) return
      const id = `taskly-mm-${Date.now()}-${index}-${Math.random().toString(36).slice(2, 9)}`
      mermaid
        .render(id, graph)
        .then(({ svg }) => {
          const div = document.createElement('div')
          div.className = 'mermaid-container markdown-body'
          div.innerHTML = svg
          pre.replaceWith(div)
        })
        .catch(() => {
          /* keep pre/code fallback */
        })
    })
  })
}

function formatActivityTail(log, maxChars) {
  const s = log || ''
  if (s.length <= maxChars) return s
  return '…' + s.slice(s.length - maxChars)
}

function escapeHtml(unsafe) {
  if (!unsafe) return ''
  return String(unsafe)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;')
}

/** WPS 演示：Slides / Shapes 等集合的 Count 可能是包装对象 */
function wpsPptCollectionCount(collection) {
  if (!collection) return 0
  let count = collection.Count
  if (typeof count !== 'number') count = count != null && count.value !== undefined ? count.value : 0
  return count
}

/** 解析与后端 PptOpenXmlHelpers.ReorderSlides 一致的 newOrder（1-based，逗号分隔） */
function parsePptReorder1Based(newOrderStr, n) {
  if (!newOrderStr || !String(newOrderStr).trim()) return { err: '[错误] 请提供 newOrder，如 2,3,1。' }
  const parts = String(newOrderStr)
    .split(',')
    .map((x) => x.trim())
    .filter(Boolean)
  const order = parts.map((p) => parseInt(p, 10))
  if (order.some((x) => isNaN(x))) return { err: '[错误] newOrder 中含无法解析的序号。' }
  if (order.length !== n) return { err: '[错误] newOrder 长度须等于当前幻灯片张数（' + n + '）。' }
  const set = new Set(order)
  if (set.size !== n) return { err: '[错误] newOrder 中序号须为 1～' + n + ' 且无重复。' }
  for (const x of order) {
    if (x < 1 || x > n) return { err: '[错误] newOrder 中序号须在 1～' + n + ' 之间。' }
  }
  return { order }
}

/** rowsCsv：| 分行，英文逗号分列（与后端一致） */
function parsePptRowsCsv(rowsCsv) {
  if (!rowsCsv || !String(rowsCsv).trim()) return { err: '[错误] 请提供 rowsCsv。' }
  const rows = String(rowsCsv)
    .split('|')
    .map((x) => x.trim())
    .filter((x) => x.length > 0)
  return {
    rows: rows.map((line) =>
      line.split(',').map((cell) => String(cell !== undefined ? cell : '').trim())
    ),
  }
}

function wpsPptNotesPlainText(notesPage) {
  if (!notesPage || !notesPage.Shapes) return ''
  const parts = []
  const n = wpsPptCollectionCount(notesPage.Shapes)
  for (let i = 1; i <= n; i++) {
    const sh = notesPage.Shapes.Item(i)
    if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
      parts.push(String(sh.TextFrame.TextRange.Text))
    }
  }
  return parts.join('\n').trim()
}

function wpsPptSetNotesPlainText(notesPage, text) {
  if (!notesPage || !notesPage.Shapes) return
  const n = wpsPptCollectionCount(notesPage.Shapes)
  for (let i = 1; i <= n; i++) {
    const sh = notesPage.Shapes.Item(i)
    if (sh && sh.TextFrame && sh.TextFrame.TextRange) {
      sh.TextFrame.TextRange.Text = text
      return
    }
  }
  if (n > 0) {
    const sh = notesPage.Shapes.Item(1)
    if (sh && sh.TextFrame && sh.TextFrame.TextRange) sh.TextFrame.TextRange.Text = text
  }
}

function wpsPptFindFirstTableShape(slide) {
  if (!slide || !slide.Shapes) return null
  const n = wpsPptCollectionCount(slide.Shapes)
  for (let i = 1; i <= n; i++) {
    const sh = slide.Shapes.Item(i)
    if (!sh) continue
    if (sh.HasTable) return sh
    try {
      if (sh.Table) return sh
    } catch (_) {
      /* ignore */
    }
  }
  return null
}

function wpsPptPickTextShape(slide, shapeIndex, shapeName) {
  if (!slide || !slide.Shapes) return null
  const n = wpsPptCollectionCount(slide.Shapes)
  if (shapeIndex > 0 && shapeIndex <= n) {
    const cand = slide.Shapes.Item(shapeIndex)
    if (cand && cand.TextFrame && cand.TextFrame.TextRange) return cand
  }
  if (shapeName) {
    for (let si = 0; si < n; si++) {
      const it = slide.Shapes.Item(si + 1)
      if (it && it.Name && String(it.Name).toLowerCase() === shapeName.toLowerCase() && it.TextFrame && it.TextFrame.TextRange) {
        return it
      }
    }
  }
  return null
}

export function useCopilot() {
  /** set_context 去重：wpsHostKind + pageTitle 与上次一致则跳过 */
  let lastSetContextSig = ''

  const connected = ref(false)
  const messages = ref([])
  const currentRound = ref(null) // { streamContent, timelineSegments, isStreaming, ... }
  const inputText = ref('')
  const inputEnabled = ref(true)

  /** 与 Chrome / Office 输入区圆环一致：SVG stroke-dasharray 用周长 */
  const CONTEXT_USAGE_RING_R = 13
  const contextUsageRingC = 2 * Math.PI * CONTEXT_USAGE_RING_R
  const contextUsageRing = ref({
    show: false,
    dashArray: contextUsageRingC,
    dashOffset: contextUsageRingC,
    title: ''
  })

  const planPanelVisible = ref(false)
  const planId = ref(null)
  const planTitle = ref('')
  const planContent = ref('')
  const planContentEdit = ref('')
  const planEditMode = ref(false)

  // Chrome 侧边栏的“执行进度（plan checklist）”在后端执行计划每步时会通过 planStepIndex 推送给前端。
  // WPS 任务窗格这里复用同一套状态结构：步骤列表 + 每步状态更新。
  const planChecklistSteps = ref([]) // [{ index: number, title: string }]
  const planChecklistStatus = ref({}) // { [index]: 'pending' | 'in_progress' | 'done' | 'fail' }
  const planChecklistDoneCount = computed(() => Object.values(planChecklistStatus.value).filter((s) => s === 'done').length)

  // 附件（图片）支持：与 chrome-extension 侧边栏保持同一类 WebSocket 协议：发送时附带 attachments（base64 + mimeType）。
  const attachments = ref([]) // [{ id, mimeType, data }]

  const hitlVisible = ref(false)
  const hitlHumanSummary = ref('')
  const hitlAction = ref('')
  const pendingConfirmId = ref(null)
  const hitlShowAddToList = ref(false)

  /** ask_options：与 Chrome sidepanel 同契约（UserOptionsManager）。 */
  const askOptionsVisible = ref(false)
  const askOptionsRequestId = ref(null)
  const askOptionsTitle = ref('')
  const askOptionsPrompt = ref('')
  const askOptionsSteps = ref([])
  const askOptionsStepIndex = ref(0)
  const askOptionsSelections = ref({})
  const askOptionsSelectedOptionId = ref(null)

  /** Agent 配置：对齐 Chrome agent-profile-select + WS agentProfileId。 */
  const agentProfileOptions = ref([])
  const activeAgentProfileId = ref('default')

  /** 历史对话：对齐 Chrome /api/chat-sessions。 */
  const historyOverlayVisible = ref(false)
  const historyItems = ref([])
  const historyLoading = ref(false)
  const historyHasMore = ref(true)
  const historySkip = ref(0)
  const historyError = ref('')

  let ws = null
  let sessionId = null
  let reconnectDelay = RECONNECT_BASE_MS
  let reconnectTimer = null
  let pendingMessages = []
  let currentToolEndIndex = 0
  let crossAgentAutoRunLock = false
  let crossAgentAutoRunQueued = false
  const CROSS_AGENT_AUTO_TRIGGER_TEXT =
    '请根据系统说明中「来自其他端的待办」逐项执行；每完成一项请调用 complete_cross_agent_task 标记完成。除待办外请勿延伸闲聊。'

  function parsePlanStepsFromContent(content) {
    // 约定：计划内容中使用 Markdown 标题形式标记步骤，例如：
    //   ## 步骤 1
    //   xxx...
    // 标题行后第一行作为该步骤标题兜底（与 Chrome 实现保持一致的解析思路）。
    if (!content || typeof content !== 'string') return []
    const steps = []
    const re = /^#{1,6}\s*步骤\s*(\d+)\s*$/gm
    const indices = []
    let m
    while ((m = re.exec(content)) !== null) indices.push({ num: parseInt(m[1], 10), pos: m.index })
    for (let i = 0; i < indices.length; i++) {
      const start = indices[i].pos
      const end = i + 1 < indices.length ? indices[i + 1].pos : content.length
      const block = content.slice(start, end).trim()
      const firstLine = (block.split(/\r?\n/)[0] || '').trim()
      const title =
        firstLine.replace(/^#{1,6}\s*步骤\s*\d+\s*/, '').trim() ||
        '步骤 ' + indices[i].num
      steps.push({ index: indices[i].num, title: title.slice(0, 60) })
    }
    return steps
  }

  function getPlanCurrentStepIndex() {
    const v = sessionStorage.getItem(STORAGE_PLAN_STEP_INDEX)
    const n = parseInt(v, 10)
    return n >= 1 ? n : 1
  }

  function setPlanCurrentStepIndex(stepIndex) {
    if (stepIndex >= 1) sessionStorage.setItem(STORAGE_PLAN_STEP_INDEX, String(stepIndex))
  }

  function clearPlanCurrentStepIndex() {
    try {
      sessionStorage.removeItem(STORAGE_PLAN_STEP_INDEX)
    } catch {
      /* ignore */
    }
  }

  function initPlanChecklistFromContent(content) {
    const steps = parsePlanStepsFromContent(content)
    planChecklistSteps.value = steps
    const nextStatus = {}
    steps.forEach((s) => {
      nextStatus[s.index] = 'pending'
    })
    planChecklistStatus.value = nextStatus
    setPlanCurrentStepIndex(1)
  }

  function cancelPlanBinding() {
    planId.value = null
    planTitle.value = ''
    planContent.value = ''
    planContentEdit.value = ''
    planEditMode.value = false
    planPanelVisible.value = false
    planChecklistSteps.value = []
    planChecklistStatus.value = {}
    clearPlanCurrentStepIndex()
    addSystemMessage('已取消当前计划绑定')
  }

  function resetConversation() {
    clearContextUsageRing()
    closeAtMode()
    // Chrome 侧边栏的“新建对话”语义：清空当前对话会话，重连 WS，并清除附件/计划绑定。
    try {
      sessionStorage.removeItem('copilot_session_id')
    } catch {
      /* ignore */
    }
    pendingMessages = []
    currentToolEndIndex = 0
    crossAgentAutoRunLock = false
    crossAgentAutoRunQueued = false
    currentRound.value = null
    messages.value = []
    inputText.value = ''
    inputEnabled.value = true
    clearAttachments()
    cancelPlanBinding()
    closeAskOptionsOverlay()
    closeHistoryOverlay()

    if (ws && ws.readyState === WebSocket.OPEN) {
      try {
        ws.close()
      } catch {
        /* ignore */
      }
    }
    ws = null
    if (reconnectTimer) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    reconnectDelay = RECONNECT_BASE_MS
    connected.value = false
    connect()
  }

  function updateChecklistStep(stepIndex, status) {
    if (!stepIndex || stepIndex < 1) return
    planChecklistStatus.value = {
      ...planChecklistStatus.value,
      [stepIndex]: status
    }
  }

  function getSessionId() {
    let id = sessionStorage.getItem('copilot_session_id')
    if (!id) {
      id = crypto.randomUUID().replace(/-/g, '').slice(0, 12)
      sessionStorage.setItem('copilot_session_id', id)
    }
    return id
  }

  try {
    activeAgentProfileId.value =
      (localStorage.getItem(STORAGE_ACTIVE_AGENT_PROFILE_ID) || 'default').trim() || 'default'
  } catch {
    activeAgentProfileId.value = 'default'
  }

  function persistActiveAgentProfileId(id) {
    const v = (id && String(id).trim()) || 'default'
    activeAgentProfileId.value = v
    try {
      localStorage.setItem(STORAGE_ACTIVE_AGENT_PROFILE_ID, v)
    } catch {
      /* ignore */
    }
  }

  /** 对齐 Chrome set_context 的 pageTitle：展示用（应用名 + 文档名）；宿主类型见 wpsHostKind 字段。 */
  function getWpsDocumentContextLabel() {
    try {
      const wpsGlobal = typeof window !== 'undefined' ? window.wps : null
      const app = wpsGlobal?.Application || (typeof window !== 'undefined' ? window.Application : null)
      if (!app) return ''
      let kind = 'WPS'
      try {
        if (app.Name) kind = String(app.Name)
      } catch {
        /* ignore */
      }
      let name = ''
      try {
        const doc = app.ActiveDocument || app.ActiveWorkbook || app.ActivePresentation
        if (doc) {
          name =
            (doc.Name && String(doc.Name).trim()) ||
            (doc.FullName && String(doc.FullName).trim()) ||
            ''
        }
      } catch {
        /* ignore */
      }
      const line = [kind, name].filter(Boolean).join(' · ')
      return line.length > 200 ? line.slice(0, 200) : line
    } catch {
      return ''
    }
  }

  /** 对齐 Chrome set_context；WPS 扩展 wpsHostKind（与 wps-rpc/hostKind 同源），供后端注入 system。 */
  function sendSetContext() {
    if (!ws || ws.readyState !== WebSocket.OPEN) return
    const w = typeof window !== 'undefined' ? window.wps : null
    const wpsHostKind = getWpsHostKind(w)
    const pageTitle = getWpsDocumentContextLabel()
    const sig = wpsHostKind + '\0' + pageTitle
    if (sig === lastSetContextSig) return
    const payload = { type: 'set_context', wpsHostKind }
    if (pageTitle) payload.pageTitle = pageTitle
    try {
      ws.send(JSON.stringify(payload))
      lastSetContextSig = sig
    } catch {
      /* ignore */
    }
  }

  function applyUiThemeChanged(msg) {
    const tid = (msg && msg.uiThemeId && String(msg.uiThemeId).trim()) || ''
    if (!tid || typeof window === 'undefined' || typeof window.TasklyTheme === 'undefined') return
    try {
      window.TasklyTheme.setTheme(tid)
      tasklyRefreshEmbedThemes()
      scheduleMermaidRun()
    } catch {
      /* ignore */
    }
  }

  /** 在系统浏览器中打开 Chrome 扩展选项页：优先使用本机 user-config 中的 chromeExtensionId（由 Chrome 选项页写入），其次构建期 VITE_CHROME_EXTENSION_ID。 */
  async function openOfficeCopilotSettingsInChrome() {
    await ensureApiBase()
    await ensureBootstrapAuthToken()
    let id = ''
    try {
      const res = await tasklyFetch(API_BASE + '/api/config')
      if (res.ok) {
        const j = await res.json().catch(() => ({}))
        id = String(j.chromeExtensionId || j.ChromeExtensionId || '').trim()
      }
    } catch {
      /* ignore */
    }
    if (!id) id = (import.meta.env.VITE_CHROME_EXTENSION_ID || '').trim()
    const url = id ? `chrome-extension://${id}/options.html` : ''
    if (!url) {
      alert(
        '未找到 Chrome 扩展 ID，无法拼出选项页地址。\n\n' +
          '请先在 Chrome 中打开本扩展的「选项」页一次（扩展会自动把 ID 写入本机配置），或在 wps-addin-new 的 .env.development.local 中设置 VITE_CHROME_EXTENSION_ID。\n\n' +
          '扩展 ID：Chrome → 扩展程序 → 开启「开发者模式」→ 卡片上的「ID」字段。'
      )
      return
    }
    const opened = window.open(url, '_blank', 'noopener,noreferrer')
    if (!opened) {
      const copied = await copyTextToClipboard(url)
      if (copied) {
        alert(
          '无法自动打开新窗口（可能被拦截）。\n\n' +
            '选项页链接已复制到剪贴板，请在 Chrome 地址栏按 Ctrl+V 粘贴后回车打开。\n\n' +
            '若未粘贴成功，请手动复制下面整行：\n' +
            url
        )
      } else {
        alert(
          '无法自动打开新窗口（可能被拦截），且当前环境无法写入剪贴板。\n\n' +
            '请手动全选复制下面整行，粘贴到 Chrome 地址栏：\n\n' +
            url
        )
      }
    }
  }

  /** 折叠某类工具参数草稿并去掉标题「（生成中）」——保留时间线顺序，不删段（对齐 Chrome finalizeOpenToolDraftSeg） */
  function finalizeToolDraftSegmentsOfKind(r, kind) {
    if (!r || !Array.isArray(r.timelineSegments)) return
    for (const s of r.timelineSegments) {
      if (s.kind !== kind) continue
      if (s.body != null && typeof s.body === 'string') {
        s.body = decodeJsonStyleUnicodeEscapes(s.body)
      }
      s.open = false
      if (s.title && String(s.title).includes('生成中')) {
        s.title = String(s.title).replace('（生成中）', '')
      }
      if (s.body != null && typeof s.body === 'string') {
        s.tail = formatActivityTail(s.body, TIMELINE_TAIL_MAX)
      }
    }
  }

  function finalizeSingleToolDraftSeg(seg) {
    if (!seg) return
    if (seg.kind !== 'tool-draft' && seg.kind !== 'subtask-tool-draft') return
    if (seg.body != null && typeof seg.body === 'string') {
      seg.body = decodeJsonStyleUnicodeEscapes(seg.body)
      seg.tail = formatActivityTail(seg.body, TIMELINE_TAIL_MAX)
    }
    seg.open = false
    if (seg.title && String(seg.title).includes('生成中')) {
      seg.title = String(seg.title).replace('（生成中）', '')
    }
  }

  function findOpenToolDraftSeg(r, kind) {
    const arr = r.timelineSegments || []
    for (let i = arr.length - 1; i >= 0; i--) {
      const s = arr[i]
      if (s.kind === kind && s.open) return s
    }
    return null
  }

  function appendToolCallDelta(msg) {
    const callIdRaw = msg.toolCallId != null ? String(msg.toolCallId).trim() : ''
    const callId = callIdRaw || '_'
    const name = msg.toolName != null ? String(msg.toolName) : ''
    const delta = msg.argumentsDelta != null ? String(msg.argumentsDelta) : ''
    if (!delta && !String(name).trim()) return
    const r = ensureRound()
    const isSub = msg.isSubtask === true
    const kind = isSub ? 'subtask-tool-draft' : 'tool-draft'
    const title = isSub ? '子任务 · 工具参数（生成中）' : '工具参数（生成中）'
    let seg = findOpenToolDraftSeg(r, kind)
    if (!seg) {
      seg = newTimelineSeg(kind, title)
      seg.lastCallId = ''
    }
    const idChanged = seg.lastCallId !== callId
    if (idChanged && String(seg.body || '').trim().length) {
      finalizeSingleToolDraftSeg(seg)
      seg = newTimelineSeg(kind, title)
      seg.lastCallId = ''
    }
    if (idChanged) {
      if (seg.body) seg.body += '\n\n'
      seg.body += '[' + callId + ']' + (String(name).trim() ? ' ' + String(name).trim() : '') + '\n'
      seg.lastCallId = callId
    }
    seg.body += delta
    seg.tail = formatActivityTail(seg.body, TIMELINE_TAIL_MAX)
  }

  function closeAskOptionsOverlay() {
    askOptionsVisible.value = false
    askOptionsRequestId.value = null
    askOptionsSteps.value = []
    askOptionsStepIndex.value = 0
    askOptionsSelections.value = {}
    askOptionsSelectedOptionId.value = null
    askOptionsTitle.value = ''
    askOptionsPrompt.value = ''
  }

  function sendAskOptionsResponse() {
    const id = askOptionsRequestId.value
    const selections = { ...askOptionsSelections.value }
    if (!id) {
      closeAskOptionsOverlay()
      return
    }
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage('连接已断开，无法提交候选项选择。', true)
      closeAskOptionsOverlay()
      inputEnabled.value = true
      return
    }
    try {
      ws.send(
        JSON.stringify({
          type: 'ask_options_response',
          id,
          selections
        })
      )
    } catch {
      addBotMessage('提交候选项失败。', true)
    }
    closeAskOptionsOverlay()
    inputEnabled.value = true
  }

  function handleAskOptionsRequest(msg) {
    try {
      const id = msg.id || msg.requestId
      if (!id) return
      const steps = Array.isArray(msg.steps) ? msg.steps : []
      if (!steps.length) {
        askOptionsRequestId.value = id
        askOptionsSelections.value = {}
        sendAskOptionsResponse()
        return
      }
      askOptionsRequestId.value = id
      askOptionsTitle.value = String(msg.title || '')
      askOptionsPrompt.value = String(msg.prompt || '')
      askOptionsSteps.value = steps
      askOptionsStepIndex.value = 0
      askOptionsSelections.value = {}
      askOptionsSelectedOptionId.value = null
      askOptionsVisible.value = true
      inputEnabled.value = false
    } catch (e) {
      console.error('ask_options_request', e)
      addBotMessage('弹出候选项选择失败，请重试。', true)
      closeAskOptionsOverlay()
      inputEnabled.value = true
    }
  }

  function setAskOptionsActiveOption(optionId) {
    askOptionsSelectedOptionId.value = optionId != null ? String(optionId) : null
  }

  function confirmAskOptionsStep() {
    const steps = askOptionsSteps.value
    const idx = askOptionsStepIndex.value
    if (!steps.length) {
      sendAskOptionsResponse()
      return
    }
    const step = steps[idx]
    if (!step) return
    const sid = step.stepId != null ? String(step.stepId) : ''
    const oid = askOptionsSelectedOptionId.value
    if (!oid) {
      alert('请先选择一个选项后再点击确定。')
      return
    }
    const nextSel = { ...askOptionsSelections.value, [sid]: String(oid) }
    askOptionsSelections.value = nextSel
    if (idx < steps.length - 1) {
      askOptionsStepIndex.value = idx + 1
      askOptionsSelectedOptionId.value = null
    } else {
      sendAskOptionsResponse()
    }
  }

  async function loadAgentProfilesFromServer() {
    await ensureApiBase()
    const res = await tasklyFetch(API_BASE + '/api/config')
    const data = await res.json().catch(() => ({}))
    if (!res.ok) return
    const list = data.agentProfiles || data.AgentProfiles || []
    let normalized = Array.isArray(list)
      ? list.map((p) => ({
          id: String((p.id ?? p.Id ?? '') || '').trim() || 'default',
          displayName: String((p.displayName ?? p.DisplayName ?? p.id) || '助手').trim()
        }))
      : []
    if (normalized.length === 0) normalized = [{ id: 'default', displayName: '默认助手' }]
    agentProfileOptions.value = normalized
    const serverDefault = String(data.activeAgentProfileId || data.ActiveAgentProfileId || 'default').trim() || 'default'
    let cur = activeAgentProfileId.value
    if (!normalized.some((p) => p.id === cur)) cur = serverDefault
    if (!normalized.some((p) => p.id === cur)) cur = normalized[0].id
    persistActiveAgentProfileId(cur)
    const themeFromServer =
      (data.uiThemeId && String(data.uiThemeId).trim()) ||
      (data.UiThemeId && String(data.UiThemeId).trim()) ||
      ''
    if (themeFromServer && typeof window !== 'undefined' && typeof window.TasklyTheme !== 'undefined') {
      try {
        window.TasklyTheme.setTheme(themeFromServer)
        tasklyRefreshEmbedThemes()
      } catch {
        /* ignore */
      }
    }
  }

  function applyAgentProfileChange(newId) {
    persistActiveAgentProfileId(newId)
    try {
      sessionStorage.removeItem('copilot_session_id')
    } catch {
      /* ignore */
    }
    sessionId = getSessionId()
    pendingMessages = []
    currentToolEndIndex = 0
    crossAgentAutoRunLock = false
    crossAgentAutoRunQueued = false
    currentRound.value = null
    messages.value = []
    inputText.value = ''
    inputEnabled.value = true
    clearAttachments()
    cancelPlanBinding()
    closeAskOptionsOverlay()
    closeHistoryOverlay()
    if (ws && ws.readyState === WebSocket.OPEN) {
      try {
        ws.close()
      } catch {
        /* ignore */
      }
    }
    ws = null
    if (reconnectTimer) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    reconnectDelay = RECONNECT_BASE_MS
    connected.value = false
    connect()
  }

  function closeHistoryOverlay() {
    historyOverlayVisible.value = false
    historyError.value = ''
  }

  async function fetchHistoryPage(append) {
    if (historyLoading.value) return
    if (append && !historyHasMore.value) return
    historyLoading.value = true
    try {
      await ensureApiBase()
      await ensureBootstrapAuthToken()
      const skip = append ? historySkip.value : 0
      if (!append) {
        historySkip.value = 0
        historyHasMore.value = true
        historyItems.value = []
      }
      const ap = activeAgentProfileId.value || 'default'
      const res = await tasklyFetch(
        API_BASE +
          '/api/chat-sessions?skip=' +
          encodeURIComponent(skip) +
          '&take=10&agentProfileId=' +
          encodeURIComponent(ap)
      )
      const data = await res.json().catch(() => ({}))
      if (!res.ok) throw new Error(data.message || '加载历史列表失败')
      const items = data.items || []
      historyHasMore.value = !!data.hasMore
      historySkip.value = skip + items.length
      historyItems.value = append ? [...historyItems.value, ...items] : items
    } catch (e) {
      historyError.value = e.message || String(e)
    } finally {
      historyLoading.value = false
    }
  }

  async function openHistoryOverlay() {
    historyOverlayVisible.value = true
    historyError.value = ''
    await fetchHistoryPage(false)
  }

  async function deleteHistorySession(sid) {
    if (!sid) return
    if (!confirm('确定删除此历史对话？本地保存的记录将移除，且无法恢复。')) return
    try {
      await ensureApiBase()
      await ensureBootstrapAuthToken()
      const res = await tasklyFetch(API_BASE + '/api/chat-sessions/' + encodeURIComponent(sid), {
        method: 'DELETE'
      })
      const data = await res.json().catch(() => ({}))
      if (!res.ok) {
        alert(data.message || '删除失败')
        return
      }
      historyItems.value = historyItems.value.filter((it) => it.sessionId !== sid)
      if (getSessionId() === sid) {
        try {
          sessionStorage.removeItem('copilot_session_id')
        } catch {
          /* ignore */
        }
        sessionId = getSessionId()
        cancelPlanBinding()
        messages.value = []
        currentRound.value = null
        if (ws) {
          try {
            ws.close()
          } catch {
            /* ignore */
          }
        }
        ws = null
        connect()
      }
    } catch (e) {
      alert(e.message || String(e))
    }
  }

  async function switchToHistorySession(sid, agentProfileIdFromItem) {
    if (!sid) return
    finalizeStream()
    try {
      await ensureApiBase()
      await ensureBootstrapAuthToken()
      if (agentProfileIdFromItem != null && String(agentProfileIdFromItem).trim() !== '') {
        persistActiveAgentProfileId(String(agentProfileIdFromItem).trim())
      }
      const res = await tasklyFetch(API_BASE + '/api/chat-sessions/' + encodeURIComponent(sid) + '/messages')
      const data = await res.json().catch(() => ({}))
      if (!res.ok) {
        addBotMessage(data.message || '加载该对话消息失败', true)
        return
      }
      try {
        sessionStorage.setItem('copilot_session_id', sid)
      } catch {
        /* ignore */
      }
      sessionId = sid
      cancelPlanBinding()
      clearAttachments()
      const msgs = data.messages || []
      messages.value = []
      for (let i = 0; i < msgs.length; i++) {
        const m = msgs[i]
        const r = (m.role || '').toLowerCase()
        if (r === 'user') addUserMessage(m.text || '')
        else addBotMessage(m.text || '', false)
      }
      closeHistoryOverlay()
      if (ws) {
        try {
          ws.close()
        } catch {
          /* ignore */
        }
      }
      ws = null
      connect()
    } catch (e) {
      addBotMessage(e.message || String(e), true)
    }
  }

  function removeWelcome() {
    messages.value = messages.value.filter((m) => m.type !== 'welcome')
  }

  function addSystemMessage(text) {
    removeWelcome()
    messages.value.push({ type: 'system', content: text })
  }

  function addUserMessage(text) {
    removeWelcome()
    messages.value.push({ type: 'user', content: text || '' })
  }

  function addBotMessage(text, isError = false) {
    removeWelcome()
    const raw = (isError && text ? '⚠️ ' : '') + (text || '')
    const html = typeof marked.parse === 'function' ? marked.parse(raw) : raw
    messages.value.push({ type: 'bot', content: text, isError, html })
    scheduleMermaidRun()
  }

  function ensureRound() {
    if (!currentRound.value) beginStream()
    return currentRound.value
  }

  function collapseAllOpenPhases() {
    const r = currentRound.value
    if (!r) return
    for (const k of ['openPrep', 'openThink', 'openDigest', 'openIntent', 'openAnswer']) {
      if (r[k]) {
        r[k].open = false
        r[k] = null
      }
    }
  }

  /** 工具开始时保留推理段展开引用，与 Chrome collapsePhasesForToolStart 一致 */
  function collapsePhasesForToolStart() {
    const r = currentRound.value
    if (!r) return
    for (const k of ['openPrep', 'openDigest', 'openIntent', 'openAnswer']) {
      if (r[k]) {
        r[k].open = false
        r[k] = null
      }
    }
  }

  function findSpliceIndexForBlockSeq(segments, blockSeq) {
    for (let i = 0; i < segments.length; i++) {
      const bs = segments[i].blockSeq
      if (typeof bs === 'number' && bs > blockSeq) return i
    }
    return segments.length
  }

  function newTimelineSeg(kind, title) {
    const r = ensureRound()
    const id = ++r.nextSegId
    const seg = { id, kind, title, body: '', tail: '', open: true, parsedHtml: '' }
    r.timelineSegments.push(seg)
    return seg
  }

  /** 带 blockSeq 的 think/answer 段，按序号插入 timelineSegments */
  function newTimelineSegOrdered(kind, title, blockSeq) {
    const r = ensureRound()
    const id = ++r.nextSegId
    const seg = { id, kind, title, body: '', tail: '', open: true, parsedHtml: '', blockSeq }
    const idx = findSpliceIndexForBlockSeq(r.timelineSegments, blockSeq)
    r.timelineSegments.splice(idx, 0, seg)
    return seg
  }

  function appendPrepLine(text) {
    const t = (text && String(text).trim()) || ''
    if (!t) return
    const r = ensureRound()
    if (!r.openPrep) r.openPrep = newTimelineSeg('prep', '准备 / 状态')
    const seg = r.openPrep
    if (seg.body) seg.body += '\n'
    seg.body += t
    seg.tail = formatActivityTail(seg.body, TIMELINE_TAIL_MAX)
  }

  function appendAgentTrace(msg) {
    const title =
      (msg.traceTitle && String(msg.traceTitle).trim()) ||
      (msg.content && String(msg.content).trim()) ||
      ''
    const detail = (msg.traceDetail && String(msg.traceDetail).trim()) || ''
    const cat = (msg.traceCategory && String(msg.traceCategory).trim()) || 'trace'
    if (!title && !detail) return
    let block = `[${cat}] ${title || '(无标题)'}`
    if (detail) block += `\n${detail}`
    appendPrepLine(block)
  }

  /** 将子任务内收集的推理/流式/工具摘要写入主时间线一条段（对齐 Chrome sidepanel.js#subtask_end） */
  function flushSubtaskUiToTimeline() {
    const r = currentRound.value
    if (!r || !r.subtaskUi) return
    const u = r.subtaskUi
    const lines = []
    if (u.looseThink && String(u.looseThink).trim()) lines.push('【推理】\n' + String(u.looseThink).trim())
    const order = Array.isArray(u.thinkOrder) ? u.thinkOrder.slice().sort((a, b) => a.blockSeq - b.blockSeq) : []
    for (const p of order) {
      if (p.body && String(p.body).trim()) lines.push('【推理#' + p.blockSeq + '】\n' + String(p.body).trim())
    }
    if (u.stream && String(u.stream).trim()) lines.push('【输出】\n' + String(u.stream).trim())
    for (const t of u.tools || []) {
      const st = t.status === 'done' ? '✓' : t.status === 'fail' ? '✗' : '…'
      lines.push(`【工具 ${st}】${t.displayLabel || t.label || ''}\n${(t.output || '').trim()}`)
    }
    const body = lines.filter(Boolean).join('\n\n')
    const seg = newTimelineSeg('subtask', u.label)
    seg.body = body || '（子任务结束）'
    seg.tail = formatActivityTail(seg.body, TIMELINE_TAIL_MAX)
    r.subtaskUi = null
  }

  function appendReasoningChunk(text, blockSeq, blockKind, isSubtask) {
    const t = text != null ? String(text) : ''
    if (!t) return
    const r = ensureRound()
    if (isSubtask === true) {
      if (!r.subtaskUi) return
      const useBlock =
        typeof blockSeq === 'number' && Number.isFinite(blockSeq) && blockKind === 'think'
      if (useBlock) {
        let part = r.subtaskUi.thinkBySeq[String(blockSeq)]
        if (!part) {
          part = { blockSeq, body: '' }
          r.subtaskUi.thinkBySeq[String(blockSeq)] = part
          r.subtaskUi.thinkOrder.push(part)
        }
        part.body += t
        return
      }
      r.subtaskUi.looseThink = (r.subtaskUi.looseThink || '') + t
      return
    }
    const useBlock =
      typeof blockSeq === 'number' && Number.isFinite(blockSeq) && blockKind === 'think'
    if (useBlock) {
      let seg = r.timelineSegments.find((s) => s.kind === 'think' && s.blockSeq === blockSeq)
      if (!seg) {
        seg = newTimelineSegOrdered('think', '推理', blockSeq)
      }
      r.openThink = seg
      seg.body += t
      seg.tail = formatActivityTail(seg.body, TIMELINE_TAIL_MAX)
      return
    }
    if (!r.openThink) r.openThink = newTimelineSeg('think', '推理')
    const seg = r.openThink
    seg.body += t
    seg.tail = formatActivityTail(seg.body, TIMELINE_TAIL_MAX)
  }

  function beginStream() {
    removeWelcome()
    currentRound.value = {
      streamContent: '',
      timelineSegments: [],
      streamWarnings: [],
      isStreaming: true,
      nextSegId: 0,
      openPrep: null,
      openThink: null,
      openDigest: null,
      openIntent: null,
      openAnswer: null,
      toolSegQueue: [],
      subtaskUi: null
    }
    currentToolEndIndex = 0
    inputEnabled.value = false
  }

  function clearContextUsageRing() {
    const c = 2 * Math.PI * CONTEXT_USAGE_RING_R
    contextUsageRing.value = { show: false, dashArray: c, dashOffset: c, title: '' }
  }

  /** Token 用量：输入区圆环 + 可选时间线段（由 WS handler 调用 appendOpenAiStreamDiagSeg）。 */
  function applyStreamUsageRing(content) {
    const c = 2 * Math.PI * CONTEXT_USAGE_RING_R
    const parsed = parseStreamUsagePayload(content)
    if (!parsed) {
      contextUsageRing.value = { show: false, dashArray: c, dashOffset: c, title: '' }
      return
    }
    const fill = usagePromptFillRatio(parsed)
    if (fill == null) {
      contextUsageRing.value = { show: false, dashArray: c, dashOffset: c, title: '' }
      return
    }
    contextUsageRing.value = {
      show: true,
      dashArray: c,
      dashOffset: c * (1 - Math.min(1, Math.max(0, fill))),
      title: buildStreamUsageRingTitle(parsed)
    }
  }

  /** OpenAI 兼容流：usage / finish / role / meta（与 stream_chunk 同级主时间线）。 */
  function appendOpenAiStreamDiagSeg(wsType, content, blockSeq, blockKind) {
    const body = content != null ? String(content).trim() : ''
    if (!body) return
    const r = ensureRound()
    const meta =
      wsType === 'stream_usage'
        ? { kind: 'streamUsage', title: 'Token 用量' }
        : wsType === 'stream_finish'
          ? { kind: 'streamFinish', title: '结束原因' }
          : wsType === 'stream_role'
            ? { kind: 'streamRole', title: '角色' }
            : wsType === 'stream_meta'
              ? { kind: 'streamMeta', title: '响应元数据' }
              : null
    if (!meta) return
    const useOrdered =
      typeof blockSeq === 'number' &&
      Number.isFinite(blockSeq) &&
      (blockKind === 'usage' || blockKind === 'finish' || blockKind === 'role' || blockKind === 'meta')
    const seg = useOrdered
      ? newTimelineSegOrdered(meta.kind, meta.title, blockSeq)
      : newTimelineSeg(meta.kind, meta.title)
    seg.body = body
    seg.tail = formatActivityTail(body.replace(/\s+/g, ' ').trim(), TIMELINE_TAIL_MAX)
  }

  function appendStreamChunk(text, blockSeq, blockKind) {
    if (!currentRound.value) beginStream()
    const r = currentRound.value
    const chunk = text != null ? String(text) : ''
    const useBlock =
      typeof blockSeq === 'number' && Number.isFinite(blockSeq) && blockKind === 'answer'

    if (useBlock) {
      if (!chunk) return
      r.timelineSegments.forEach((s) => {
        if (
          s.kind === 'think' &&
          typeof s.blockSeq === 'number' &&
          s.blockSeq < blockSeq
        ) {
          s.open = false
        }
      })
      r.openThink = null
      if (r.openDigest) {
        r.openDigest.open = false
        r.openDigest = null
      }
      r.streamContent += chunk
      let a = r.timelineSegments.find((s) => s.kind === 'answer' && s.blockSeq === blockSeq)
      if (!a) {
        a = newTimelineSegOrdered('answer', '助手回复', blockSeq)
      }
      r.openAnswer = a
      a.body += chunk
      a.parsedHtml =
        typeof marked.parse === 'function' ? marked.parse(a.body) : a.body
      a.tail = formatActivityTail(a.body.replace(/\s+/g, ' ').trim(), TIMELINE_TAIL_MAX)
      r.parsedHtml =
        typeof marked.parse === 'function' ? marked.parse(r.streamContent) : r.streamContent
      return
    }

    if (!chunk) return
    if (r.openThink) {
      r.openThink.open = false
      r.openThink = null
    }
    if (r.openDigest) {
      r.openDigest.open = false
      r.openDigest = null
    }
    r.streamContent += chunk
    if (!r.openAnswer) {
      r.openAnswer = newTimelineSeg('answer', '助手回复')
    }
    const a = r.openAnswer
    a.body += chunk
    a.parsedHtml =
      typeof marked.parse === 'function' ? marked.parse(a.body) : a.body
    a.tail = formatActivityTail(a.body.replace(/\s+/g, ' ').trim(), TIMELINE_TAIL_MAX)
    r.parsedHtml =
      typeof marked.parse === 'function' ? marked.parse(r.streamContent) : r.streamContent
  }

  function finalizeStream() {
    if (askOptionsVisible.value) {
      closeAskOptionsOverlay()
      inputEnabled.value = true
    }
    if (currentRound.value) {
      const round = currentRound.value
      flushSubtaskUiToTimeline()
      collapseAllOpenPhases()
      round.openAnswer = null
      finalizeToolDraftSegmentsOfKind(round, 'tool-draft')
      finalizeToolDraftSegmentsOfKind(round, 'subtask-tool-draft')
      round.timelineSegments.forEach((s) => {
        s.open = false
      })
      const answerSegs = round.timelineSegments.filter((s) => s.kind === 'answer')
      if (answerSegs.length) answerSegs[answerSegs.length - 1].open = true
      round.isStreaming = false
      round.parsedHtml = typeof marked.parse === 'function' ? marked.parse(round.streamContent) : round.streamContent
      removeWelcome()
      // 保留各段的 open（尤其最后一条「助手回复」应为展开），勿在入列时一律 false —— 否则历史轮总结永远折叠。
      const segs = round.timelineSegments.map((s) => ({ ...s }))
      messages.value.push({
        type: 'round',
        streamContent: round.streamContent,
        parsedHtml: round.parsedHtml,
        timelineSegments: segs,
        streamWarnings: [...(round.streamWarnings || [])],
        isStreaming: false
      })
      currentRound.value = null
    }
    inputEnabled.value = true
    if (crossAgentAutoRunLock) {
      crossAgentAutoRunLock = false
    }
    if (crossAgentAutoRunQueued && !currentRound.value?.isStreaming) {
      crossAgentAutoRunQueued = false
      scheduleCrossAgentAutoRun()
    }
    scheduleMermaidRun()
  }

  function setInputEnabled(enabled) {
    inputEnabled.value = enabled
  }

  function connect() {
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return
    ensureApiBase()
      .then(() => {
        sessionId = getSessionId()
        return ensureBootstrapAuthToken()
      })
      .then((bootstrap) => {
    const tok = getStoredAuthToken()
    const ap = (activeAgentProfileId.value || 'default').trim() || 'default'
    const qs = buildWebSocketQueryString({
      sessionId,
      clientType: CLIENT_TYPE,
      agentProfileId: ap,
      token: tok,
      bootstrap,
      telemetryDeviceIdKey: TASKLY_TELEMETRY_DEVICE_ID_KEY,
      telemetryClientEmissionKey: TASKLY_TELEMETRY_CLIENT_EMISSION_KEY,
      telemetryRelayActiveProfileKey: TASKLY_TELEMETRY_RELAY_ACTIVE_PROFILE_KEY,
      telemetryEventKindsByProfileKey: TASKLY_TELEMETRY_EVENT_KINDS_BY_PROFILE_KEY
    })
    ws = new WebSocket(WS_URL + qs)
    ws.onopen = () => {
      reconnectDelay = RECONNECT_BASE_MS
      connected.value = true
      addSystemMessage('已连接到本地服务')
      lastSetContextSig = ''
      sendSetContext()
      while (pendingMessages.length > 0) {
        const m = pendingMessages.shift()
        send(m.text, m.attachmentsPayload || null)
      }
      flushCrossAgentAutoRunAfterReconnect()
    }
    ws.onmessage = (e) => handleMessage(e.data)
    ws.onclose = () => {
      const wasStreaming = !!currentRound.value?.isStreaming
      ws = null
      connected.value = false
      finalizeStream()
      if (wasStreaming) addBotMessage('连接已断开，请检查网络或稍后重试。', true)
      scheduleReconnect()
    }
    ws.onerror = () => {
      if (ws) ws.close()
    }
    })
      .catch((e) => {
        addSystemMessage('找不到本机 Office Copilot：' + (e && e.message ? e.message : String(e)))
      })
  }

  function scheduleReconnect() {
    if (reconnectTimer) return
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS)
      connect()
    }, reconnectDelay)
  }

  function send(text, attachmentsPayload = null, sendOpts = {}) {
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage('连接已断开，消息未发送。请检查后台是否启动，并在 Chrome 扩展中配置。', true)
      return
    }
    sendSetContext()
    const skipPlan = sendOpts && sendOpts.skipPlan === true
    const payload = { type: 'text', content: text || '' }
    if (!skipPlan && planId.value) {
      payload.mode = 'agent'
      payload.planId = planId.value
      payload.planCurrentStepIndex = getPlanCurrentStepIndex()
    }
    if (attachmentsPayload && attachmentsPayload.length > 0) {
      payload.attachments = attachmentsPayload
    }
    ws.send(JSON.stringify(payload))
  }

  function scheduleCrossAgentAutoRun() {
    if (crossAgentAutoRunLock) {
      crossAgentAutoRunQueued = true
      return
    }
    if (currentRound.value?.isStreaming) {
      crossAgentAutoRunQueued = true
      return
    }
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      crossAgentAutoRunQueued = true
      return
    }
    crossAgentAutoRunLock = true
    crossAgentAutoRunQueued = false
    addUserMessage(CROSS_AGENT_AUTO_TRIGGER_TEXT)
    send(CROSS_AGENT_AUTO_TRIGGER_TEXT, null, { skipPlan: true })
  }

  function onCrossAgentTaskPush(msg) {
    const tid = msg && msg.taskId != null ? String(msg.taskId) : ''
    const desc = msg && msg.description != null ? String(msg.description).trim() : ''
    let line = '已收到来自其他端的跨端任务'
    if (tid) line += '（id=' + tid + '）'
    line += '。'
    if (desc) {
      const max = 180
      line += '摘要：' + (desc.length > max ? desc.slice(0, max) + '…' : desc)
    }
    addSystemMessage(line)
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      crossAgentAutoRunQueued = true
      addSystemMessage('当前未连接，重连成功后将自动尝试执行跨端待办。')
      return
    }
    scheduleCrossAgentAutoRun()
  }

  function flushCrossAgentAutoRunAfterReconnect() {
    if (!crossAgentAutoRunQueued || crossAgentAutoRunLock || currentRound.value?.isStreaming) return
    scheduleCrossAgentAutoRun()
  }

  function handleMessage(raw) {
    let msg
    try {
      msg = JSON.parse(raw)
    } catch (e) {
      msg = { type: 'text', content: raw }
    }
    switch (msg.type) {
      case 'stream_start':
        clearContextUsageRing()
        beginStream()
        break
      case 'reasoning_chunk':
        appendReasoningChunk(msg.content, msg.blockSeq, msg.blockKind, msg.isSubtask === true)
        break
      case 'agent_phase': {
        const phase = (msg.phase && String(msg.phase)) || ''
        const c = (msg.content && String(msg.content).trim()) || ''
        if (!c) break
        const r = ensureRound()
        if (phase === 'intent') {
          collapseAllOpenPhases()
          const seg = newTimelineSeg('intent', '计划 / 意图')
          seg.body = c
          seg.tail = formatActivityTail(c, TIMELINE_TAIL_MAX)
          r.openIntent = seg
        } else if (phase === 'digest') {
          if (r.openDigest) {
            r.openDigest.open = false
            r.openDigest = null
          }
          const seg = newTimelineSeg('digest', '处理工具结果')
          seg.body = c
          seg.tail = formatActivityTail(c, TIMELINE_TAIL_MAX)
          r.openDigest = seg
        }
        break
      }
      case 'stream_chunk':
        appendStreamChunk(msg.content, msg.blockSeq, msg.blockKind)
        break
      case 'stream_usage':
        applyStreamUsageRing(msg.content)
        appendOpenAiStreamDiagSeg('stream_usage', msg.content, msg.blockSeq, msg.blockKind)
        break
      case 'stream_finish':
        appendOpenAiStreamDiagSeg('stream_finish', msg.content, msg.blockSeq, msg.blockKind)
        break
      case 'stream_role':
        appendOpenAiStreamDiagSeg('stream_role', msg.content, msg.blockSeq, msg.blockKind)
        break
      case 'stream_meta':
        appendOpenAiStreamDiagSeg('stream_meta', msg.content, msg.blockSeq, msg.blockKind)
        break
      case 'stream_end':
        finalizeStream()
        break
      case 'agent_status': {
        const line = (msg.content && String(msg.content).trim()) || ''
        if (line) appendPrepLine(line)
        break
      }
      case 'agent_trace':
        appendAgentTrace(msg)
        break
      case 'stream_warning': {
        if (!currentRound.value) beginStream()
        const t = (msg.content && String(msg.content).trim()) || '服务端返回了警告'
        currentRound.value.streamWarnings.push(t)
        break
      }
      case 'subtask_start': {
        const r0 = currentRound.value
        if (r0) finalizeToolDraftSegmentsOfKind(r0, 'subtask-tool-draft')
        const taskDesc = (msg.taskDescription && String(msg.taskDescription).trim()) || '子任务'
        const titleLen = 48
        const summaryLabel = taskDesc.length <= titleLen ? taskDesc : taskDesc.slice(0, titleLen) + '…'
        const presetRaw = msg.subtaskPreset != null ? String(msg.subtaskPreset).trim() : ''
        const presetTag =
          presetRaw === 'explore'
            ? '（探索）'
            : presetRaw === 'cliShell'
              ? '（CLI）'
              : presetRaw === 'browser'
                ? '（浏览器）'
                : ''
        const rSt = ensureRound()
        rSt.subtaskUi = {
          label: '子代理' + presetTag + '：' + summaryLabel,
          thinkBySeq: {},
          thinkOrder: [],
          looseThink: '',
          stream: '',
          tools: []
        }
        break
      }
      case 'subtask_chunk': {
        const rCh = currentRound.value
        if (rCh && rCh.subtaskUi && msg.content != null) rCh.subtaskUi.stream += String(msg.content)
        break
      }
      case 'subtask_end': {
        const rEnd = currentRound.value
        if (rEnd) finalizeToolDraftSegmentsOfKind(rEnd, 'subtask-tool-draft')
        flushSubtaskUiToTimeline()
        break
      }
      case 'tool_call_delta':
        appendToolCallDelta(msg)
        break
      case 'tool_invocation_start': {
        const r = ensureRound()
        const label = msg.summary || '正在执行: ' + (msg.plugin || '') + '.' + (msg.function || '')
        const invStart = msg.invocationId != null ? String(msg.invocationId).trim() : ''
        if (msg.isSubtask === true) {
          finalizeToolDraftSegmentsOfKind(r, 'subtask-tool-draft')
          collapsePhasesForToolStart()
          if (r.subtaskUi) {
            r.subtaskUi.tools.push({
              invocationId: invStart,
              label,
              displayLabel: label.replace(/^正在执行:\s*/i, ''),
              status: 'running',
              output: ''
            })
          }
          if (msg.planStepIndex) {
            updateChecklistStep(msg.planStepIndex, 'in_progress')
          }
          break
        }
        finalizeToolDraftSegmentsOfKind(r, 'tool-draft')
        collapsePhasesForToolStart()
        const seg = newTimelineSeg('tool', label)
        seg.status = 'running'
        seg.output = ''
        seg.label = label
        seg.displayLabel = label.replace(/^正在执行:\s*/i, '')
        if (invStart) seg.invocationId = invStart
        r.toolSegQueue.push(seg)
        if (msg.planStepIndex) {
          updateChecklistStep(msg.planStepIndex, 'in_progress')
        }
        break
      }
      case 'tool_invocation_end': {
        const r = currentRound.value
        if (!r) break
        const invEnd = msg.invocationId != null ? String(msg.invocationId).trim() : ''
        if (msg.isSubtask === true) {
          let t = null
          if (invEnd && r.subtaskUi && Array.isArray(r.subtaskUi.tools)) {
            t = r.subtaskUi.tools.find((x) => x.invocationId === invEnd)
          }
          if (!t && r.subtaskUi && Array.isArray(r.subtaskUi.tools)) {
            const idx = r.subtaskUi.tools.findIndex((x) => x.status === 'running')
            if (idx >= 0) t = r.subtaskUi.tools[idx]
          }
          if (t) {
            const contentRaw = (msg.content && String(msg.content).trim()) || ''
            const ok = msg.success === true && !toolInvocationContentLooksLikeError(contentRaw)
            t.status = ok ? 'done' : 'fail'
            t.output = decodeJsonStyleUnicodeEscapes(contentRaw)
          }
          if (msg.planStepIndex) {
            updateChecklistStep(msg.planStepIndex, msg.success === true ? 'done' : 'pending')
          }
          if (
            msg.plugin === 'Plan' &&
            msg.function === 'execute_plan_step' &&
            msg.planStepIndex &&
            msg.success === true
          ) {
            setPlanCurrentStepIndex(msg.planStepIndex + 1)
          }
          break
        }
        if (!r.toolSegQueue || !r.toolSegQueue.length) break
        let block = null
        if (invEnd) block = r.toolSegQueue.find((s) => s.invocationId === invEnd)
        if (!block) block = r.toolSegQueue[currentToolEndIndex]
        if (block) {
          const contentRaw = (msg.content && String(msg.content).trim()) || ''
          const ok = msg.success === true && !toolInvocationContentLooksLikeError(contentRaw)
          block.status = ok ? 'done' : 'fail'
          block.output = decodeJsonStyleUnicodeEscapes(contentRaw)
          block.displayLabel = (block.label || '').replace(/^正在执行:\s*/i, '')
          block.open = true
        }
        if (msg.planStepIndex) {
          updateChecklistStep(msg.planStepIndex, msg.success === true ? 'done' : 'pending')
        }
        if (
          msg.plugin === 'Plan' &&
          msg.function === 'execute_plan_step' &&
          msg.planStepIndex &&
          msg.success === true
        ) {
          setPlanCurrentStepIndex(msg.planStepIndex + 1)
        }
        currentToolEndIndex++
        break
      }
      case 'echo':
      case 'text':
        addBotMessage(msg.content)
        break
      case 'pong':
        break
      case 'error':
        finalizeStream()
        addBotMessage((msg.content && String(msg.content).trim()) || '请求失败，请稍后重试。', true)
        break
      case 'rpc_request':
        handleRpcRequest(msg)
        break
      case 'confirm_request':
        handleConfirmRequest(msg)
        break
      case 'ask_options_request':
        handleAskOptionsRequest(msg)
        break
      case 'ui_theme_changed':
        applyUiThemeChanged(msg)
        break
      case 'plan_created': {
        const planIdMsg = msg.planId || ''
        const title = msg.title || '新计划'
        const createdBy = (msg.createdBy || '').toLowerCase()
        if (planIdMsg && createdBy === CLIENT_TYPE) fetchPlanAndShow(planIdMsg, title, createdBy)
        break
      }
      case 'plan_updated': {
        const planIdUp = msg.planId || ''
        const titleUp = msg.title || planIdUp || '计划'
        const createdByUp = (msg.createdBy || '').toLowerCase()
        if (planIdUp && createdByUp === CLIENT_TYPE) {
          addSystemMessage('计划内容已更新，正在刷新任务窗格中的计划正文。')
          fetchPlanAndShow(planIdUp, titleUp, createdByUp)
        }
        break
      }
      case 'cross_agent_task':
        onCrossAgentTaskPush(msg)
        break
      case 'cross_agent_task_completed': {
        const st = (msg.status && String(msg.status)) || ''
        const rs = (msg.resultSummary && String(msg.resultSummary).trim()) || ''
        const tid = msg.taskId != null ? String(msg.taskId) : ''
        let line = '跨端任务已由对方处理' + (tid ? '（id=' + tid + '）' : '')
        if (st) line += '，状态：' + st
        line += rs ? '。' + (rs.length > 160 ? rs.slice(0, 160) + '…' : rs) : '。'
        addSystemMessage(line)
        break
      }
      default:
        addBotMessage(msg.content || JSON.stringify(msg))
    }
  }

  function handleSend() {
    const text = inputText.value.trim()
    const hasAttachments = attachments.value.length > 0
    if (!text && !hasAttachments) return
    if (currentRound.value?.isStreaming) return
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      const userMsgText = text || (hasAttachments ? '（附图片）' : '')
      addUserMessage(userMsgText)
      const attachmentsPayload = hasAttachments ? buildAttachmentsPayload() : null
      pendingMessages.push({ text, attachmentsPayload })
      addBotMessage('连接已断开，正在重连… 请确保后台已启动并在 Chrome 扩展中配置。', true)
      if (reconnectTimer) {
        clearTimeout(reconnectTimer)
        reconnectTimer = null
      }
      connect()
      inputText.value = ''
      attachments.value = []
      return
    }
    const userMsgText = text || (hasAttachments ? '（附图片）' : '')
    addUserMessage(userMsgText)
    const attachmentsPayload = hasAttachments ? buildAttachmentsPayload() : null
    send(text, attachmentsPayload)
    inputText.value = ''
    attachments.value = []
  }

  function buildAttachmentsPayload() {
    return attachments.value.map((a) => ({ mimeType: a.mimeType, data: a.data }))
  }

  function removeAttachment(id) {
    attachments.value = attachments.value.filter((a) => a.id !== id)
  }

  function clearAttachments() {
    attachments.value = []
  }

  async function addFilesAsAttachments(files) {
    const imageFiles = Array.from(files || []).filter((f) => f && f.type && f.type.startsWith('image/'))
    if (imageFiles.length === 0) return

    const tasks = imageFiles.map(
      (file) =>
        new Promise((resolve) => {
          const reader = new FileReader()
          reader.onload = () => {
            const dataUrl = String(reader.result || '')
            const match = dataUrl.match(/^data:([^;]+);base64,(.+)$/)
            const mime = match ? match[1] : file.type || 'image/png'
            const data = match ? match[2] : (dataUrl.split(',')[1] || '')
            if (data) {
              attachments.value.push({ id: Date.now() + '-' + Math.random(), mimeType: mime || 'image/png', data })
            }
            resolve()
          }
          reader.onerror = () => resolve()
          reader.readAsDataURL(file)
        })
    )
    await Promise.all(tasks)
  }

  async function handleFileInputChange(e) {
    if (!e?.target?.files) return
    await addFilesAsAttachments(e.target.files)
    // 清空同一文件再次选择无法触发 change 的问题
    try {
      e.target.value = ''
    } catch {
      /* ignore */
    }
  }

  function stopStream() {
    if (!currentRound.value?.isStreaming) return
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'stop' }))
    finalizeStream()
  }

  function fetchPlanAndShow(id, title, createdBy) {
    if (createdBy !== CLIENT_TYPE) return
    ensureApiBase().then(() =>
      tasklyFetch(API_BASE + '/api/plans/' + encodeURIComponent(id))
        .then((res) =>
          res.json().catch(() => ({})).then((data) => {
            if (!res.ok) return Promise.reject(new Error((data && data.message) || '请求失败 ' + res.status))
            return data
          })
        )
        .then((data) => {
          planId.value = id
          planTitle.value = title || (data.meta && data.meta.title) || id
          planContent.value = data.content || ''
          planContentEdit.value = data.content || ''
          planEditMode.value = false
          planPanelVisible.value = true
          initPlanChecklistFromContent(data.content || '')
        })
        .catch((e) => {
          console.error('fetch plan failed', e)
          alert(e.message || '加载计划失败')
        })
    )
  }

  function showPlanEdit() {
    planContentEdit.value = planContent.value
    planEditMode.value = true
  }

  function cancelPlanEdit() {
    planEditMode.value = false
  }

  function savePlan() {
    if (!planId.value) return
    const content = planContentEdit.value
    ensureApiBase().then(() =>
      tasklyFetch(API_BASE + '/api/plans/' + encodeURIComponent(planId.value), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content })
    })
      .then(async (res) => {
        const data = await res.json().catch(() => ({}))
        if (!res.ok) throw new Error((data && data.message) || '保存失败')
        return data
      })
      .then(() => {
        planContent.value = content
        planEditMode.value = false
        initPlanChecklistFromContent(content)
      })
      .catch((e) => {
        console.error('save plan failed', e)
        alert(e.message || '保存失败')
      })
    )
  }

  function executePlan() {
    if (planId.value) send('请按当前绑定的计划执行')
  }

  function wpsDocScriptMaxLen(p, def, cap) {
    let n = def
    if (p && p.maxLength != null) {
      const x = parseInt(String(p.maxLength), 10)
      if (!isNaN(x) && x > 0) n = Math.min(x, cap)
    }
    return n
  }

  const DOCUMENT_SCRIPTS = {
    word_read_selection() {
      if (!window.wps || !window.wps.ActiveDocument) return Promise.resolve('当前环境不可用。')
      try {
        const sel = window.wps.Selection
        let t = sel && sel.Text ? sel.Text : ''
        t = t || ''
        if (t.length > 8000) t = t.slice(0, 8000) + '\n...(已截断)'
        return Promise.resolve(t || '(无选区)')
      } catch (e) {
        return Promise.resolve('WPS 暂不支持读取选区，请使用 word_read_body。')
      }
    },
    wps_doc_meta() {
      return Promise.resolve().then(() => {
        const lines = ['clientType: wps']
        try {
          if (!window.wps) {
            lines.push('（WPS 对象不可用）')
            return lines.join('\n')
          }
          if (window.wps.ActiveDocument && window.wps.ActiveDocument.Name) {
            lines.push('文档名: ' + window.wps.ActiveDocument.Name)
            if (window.wps.ActiveDocument.FullName) lines.push('路径: ' + window.wps.ActiveDocument.FullName)
          } else if (window.wps.ActiveWorkbook && window.wps.ActiveWorkbook.Name) {
            lines.push('工作簿: ' + window.wps.ActiveWorkbook.Name)
          } else if (window.wps.Application && window.wps.Application.ActivePresentation) {
            const pr = window.wps.Application.ActivePresentation
            lines.push('演示文稿: ' + (pr.FullName || pr.Name || '(无名)'))
          } else {
            lines.push('（未能识别当前文档类型）')
          }
        } catch (e) {
          lines.push('错误: ' + (e && e.message ? e.message : String(e)))
        }
        return lines.join('\n')
      })
    },
    wps_word_body_preview(p) {
      return Promise.resolve().then(() => {
        if (!window.wps || !window.wps.Enum || !window.wps.Enum.wdStory) {
          return 'wps_word_body_preview 仅适用于 WPS 文字。当前可能不是文字组件。'
        }
        const doc = window.wps.ActiveDocument
        if (!doc || !doc.Content) return '无法获取正文。'
        const maxLen = wpsDocScriptMaxLen(p, 2000, 32000)
        let t = doc.Content.Text || ''
        const header = '[WPS 正文摘录，最多 ' + maxLen + ' 字符]\n'
        if (t.length > maxLen) t = t.slice(0, maxLen) + '\n...(已截断)'
        return header + (t || '(无正文)')
      })
    },
    wps_ppt_slide_glance() {
      return Promise.resolve().then(() => {
        try {
          const app = window.wps && window.wps.Application
          if (!app) return 'WPS Application 不可用。'
          const pres = app.ActivePresentation
          if (!pres) return '当前不是 WPS 演示，请在 WPS 演示中打开文稿后再试。'
          const win = app.ActiveWindow
          if (!win || !win.View || !win.View.Slide) return '无法获取当前幻灯片。'
          const slide = win.View.Slide
          const idx = slide.SlideIndex
          let n = pres.Slides.Count
          if (typeof n !== 'number') n = n != null && n.value !== undefined ? n.value : 0
          const parts = []
          if (slide.Shapes && slide.Shapes.Count) {
            const sc = slide.Shapes.Count
            for (let si = 1; si <= sc; si++) {
              const sh = slide.Shapes.Item(si)
              if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                let txt = String(sh.TextFrame.TextRange.Text)
                if (txt.length > 800) txt = txt.slice(0, 800) + '...'
                parts.push(txt)
              }
            }
          }
          let body = parts.join(' ').trim() || '(无文本)'
          if (body.length > 8000) body = body.slice(0, 8000) + '...'
          return '[WPS 演示 第 ' + idx + ' / ' + n + ' 页]\n' + body
        } catch (e) {
          return 'WPS 幻灯片快览失败：' + (e && e.message ? e.message : String(e))
        }
      })
    }
  }

  function handleRpcRequest(msg) {
    const id = msg.id
    const method = msg.method
    const params = msg.params || {}
    if (!id || !method) return

    function sendRes(result, err) {
      if (!ws || ws.readyState !== WebSocket.OPEN) return
      ws.send(JSON.stringify({ type: 'rpc_response', id, result: result != null ? result : null, error: err || null }))
    }

    if (method === 'run_document_script') {
      const scriptId = params.scriptId
      const scriptParams = params.scriptParams || {}
      if (!scriptId || typeof DOCUMENT_SCRIPTS[scriptId] !== 'function') {
        sendRes(null, '未知或未注册的脚本 ID: ' + (scriptId || ''))
        return
      }
      Promise.resolve(DOCUMENT_SCRIPTS[scriptId](scriptParams))
        .then((r) => sendRes(typeof r === 'string' ? r : JSON.stringify(r), null))
        .catch((err) => sendRes(null, err && err.message ? err.message : String(err)))
      return
    }

    if (method === 'run_custom_document_script') {
      const scriptCode = params.scriptCode
      if (typeof scriptCode !== 'string' || !scriptCode.trim()) {
        sendRes(null, 'run_custom_document_script 需要非空的 scriptCode 参数。')
        return
      }
      try {
        const fn = new Function(scriptCode.trim())
        const out = fn()
        if (out && typeof out.then === 'function') {
          out
            .then((r) => {
              const result = r !== undefined && r !== null && typeof r !== 'string' ? JSON.stringify(r) : (r === undefined || r === null ? '' : r)
              sendRes(result, null)
            })
            .catch((err) => sendRes(null, err && err.message ? err.message : String(err)))
        } else {
          const result = out !== undefined && out !== null && typeof out !== 'string' ? JSON.stringify(out) : (out === undefined || out === null ? '' : out)
          sendRes(result, null)
        }
      } catch (err) {
        sendRes(null, err && err.message ? err.message : String(err))
      }
      return
    }

    if (!window.wps) {
      sendRes(null, 'WPS API 不可用，请确保在 WPS 加载项环境中运行。')
      return
    }

    try {
      const hostKind = getWpsHostKind(window.wps)
      if (method.startsWith('word_')) {
        const ge = assertWpsHost('word', hostKind, method)
        if (ge) {
          sendRes(null, ge)
          return
        }
      }
      if (method.startsWith('excel_')) {
        const ge = assertWpsHost('et', hostKind, method)
        if (ge) {
          sendRes(null, ge)
          return
        }
      }
      if (method.startsWith('ppt_')) {
        const ge = assertWpsHost('wpp', hostKind, method)
        if (ge) {
          sendRes(null, ge)
          return
        }
      }

      if (method === 'word_insert_text') {
        try {
          sendRes(wordInsertTextWps(window.wps, params), null)
        } catch (e) {
          sendRes(null, e && e.message ? e.message : String(e))
        }
      } else if (method === 'word_read_body') {
        const maxLen = params.maxLength > 0 ? params.maxLength : 8000
        const doc = window.wps.ActiveDocument
        if (doc && doc.Content) {
          let t = doc.Content.Text || ''
          if (t.length > maxLen) t = t.slice(0, maxLen) + '\n...(已截断)'
          sendRes(t || '(无正文)', null)
        } else {
          sendRes(null, '无法获取当前文档正文。')
        }
      } else if (method === 'word_read_selection') {
        try {
          const sel = window.wps.Selection
          const selText = sel && sel.Text ? sel.Text : ''
          sendRes(selText || '(无选区)', null)
        } catch (e) {
          sendRes(null, 'WPS 暂不支持读取选区，请参考 WPS 开放平台 Selection API。')
        }
      } else if (method === 'word_insert_table') {
        try {
          sendRes(wordInsertTableWps(window.wps, params), null)
        } catch (e) {
          sendRes(null, 'word_insert_table 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'word_search_replace') {
        const searchText = params.searchText != null ? String(params.searchText) : ''
        const replaceText = params.replaceText != null ? String(params.replaceText) : ''
        const replaceAll = params.replaceAll !== false
        if (!searchText) {
          sendRes(null, 'searchText 不能为空')
          return
        }
        const doc = window.wps.ActiveDocument
        if (doc && doc.Content) {
          const fullText = doc.Content.Text || ''
          const newText = replaceAll ? fullText.split(searchText).join(replaceText) : fullText.replace(searchText, replaceText)
          doc.Content.Text = newText
          sendRes('成功：已完成查找替换。', null)
        } else {
          sendRes(null, '无法获取当前文档内容。')
        }
      } else if (
        method === 'excel_read_range' ||
        method === 'excel_write_range' ||
        method === 'excel_list_sheets' ||
        method === 'excel_get_used_range' ||
        method === 'excel_read_formulas' ||
        method === 'excel_write_formulas'
      ) {
        try {
          sendRes(runWpsExcelRpc(method, params, window.wps), null)
        } catch (e) {
          sendRes(null, (e && e.message ? e.message : String(e)) || 'WPS 表格 RPC 执行失败。')
        }
      } else if (method === 'ppt_slides_list') {
        try {
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres) {
            sendRes(null, '当前不是 WPS 演示文档，无法执行 ppt_slides_list。请在 WPS 演示中打开文稿后再试。')
            return
          }
          const slides = pres.Slides
          if (!slides) {
            sendRes('演示文稿中无幻灯片。', null)
            return
          }
          let count = slides.Count
          if (typeof count !== 'number') count = count != null && count.value !== undefined ? count.value : 0
          let out = '共 ' + count + ' 张幻灯片（按播放顺序）：\n'
          for (let idx = 0; idx < count; idx++) {
            const slide = slides.Item(idx + 1)
            let preview = '(无文本)'
            if (slide && slide.Shapes && slide.Shapes.Count > 0) {
              const parts = []
              for (let si = 0; si < slide.Shapes.Count; si++) {
                const sh = slide.Shapes.Item(si + 1)
                if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                  let t = String(sh.TextFrame.TextRange.Text).slice(0, 80)
                  if (t.length === 80) t += '...'
                  parts.push(t)
                }
              }
              if (parts.length > 0) preview = parts.join(' ')
            }
            out += '  ' + (idx + 1) + '. ' + preview + '\n'
          }
          sendRes(out.trim(), null)
        } catch (e) {
          sendRes(null, '当前不是 WPS 演示文档或 API 不可用：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_read') {
        try {
          const slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1
          const includeShapeDetails = params.includeShapeDetails !== false && params.includeShapeDetails !== 'false'
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          let count = pres.Slides.Count
          if (typeof count !== 'number') count = count != null && count.value !== undefined ? count.value : 0
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          const parts = []
          const shapeLines = []
          if (slide && slide.Shapes && slide.Shapes.Count > 0) {
            for (let i = 0; i < slide.Shapes.Count; i++) {
              const sh = slide.Shapes.Item(i + 1)
              if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                const tx = String(sh.TextFrame.TextRange.Text)
                parts.push(tx)
                if (includeShapeDetails) {
                  const nm = sh.Name != null ? String(sh.Name) : ''
                  let pv = tx.length > 120 ? tx.slice(0, 120) + '...' : tx
                  if (!pv) pv = '(空)'
                  shapeLines.push('  [' + (i + 1) + '] Name="' + nm + '" 预览: ' + pv)
                }
              }
            }
          }
          let text = '[幻灯片 ' + slideIndex + ']\n' + (parts.length > 0 ? parts.join(' ').trim() : '(无文本)')
          if (includeShapeDetails) {
            text += '\n\n[形状列表（编号供 shapeIndex）]\n'
            text += (shapeLines.length > 0 ? shapeLines.join('\n') : '（本页无带文本的形状）')
          }
          sendRes(text, null)
        } catch (e) {
          sendRes(null, 'ppt_slide_read 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_write') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          const placeholderType = (params.placeholderType || 'title').toString().trim().toLowerCase()
          const text = (params.text != null ? params.text : '').toString()
          const shapeIndex = params.shapeIndex != null ? parseInt(params.shapeIndex, 10) : 0
          const shapeName = (params.shapeName != null ? params.shapeName : '').toString().trim()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          let count = typeof pres.Slides.Count === 'number' ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          if (!slide || !slide.Shapes || slide.Shapes.Count < 1) {
            sendRes(null, '[错误] 未找到可写入的形状。')
            return
          }
          let shape = null
          if (shapeIndex > 0 && shapeIndex <= slide.Shapes.Count) {
            const cand = slide.Shapes.Item(shapeIndex)
            if (cand && cand.TextFrame && cand.TextFrame.TextRange) shape = cand
          }
          if (!shape && shapeName) {
            for (let si = 0; si < slide.Shapes.Count; si++) {
              const it = slide.Shapes.Item(si + 1)
              if (it && it.Name && String(it.Name).toLowerCase() === shapeName.toLowerCase() && it.TextFrame && it.TextFrame.TextRange) {
                shape = it
                break
              }
            }
          }
          if (!shape) {
            const idx = placeholderType === 'body' || placeholderType === 'subtitle' ? 2 : 1
            shape = slide.Shapes.Item(idx) || slide.Shapes.Item(1)
          }
          if (!shape || !shape.TextFrame || !shape.TextFrame.TextRange) {
            sendRes(null, '[错误] 未找到可写入的形状，请用 ppt_slide_read 查看形状编号。')
            return
          }
          shape.TextFrame.TextRange.Text = text
          sendRes('成功：已写入幻灯片文本。', null)
        } catch (e) {
          sendRes(null, 'ppt_slide_write 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_insert') {
        try {
          const position = params.position != null ? parseInt(params.position, 10) : null
          const titleText = (params.titleText != null ? params.titleText : '').toString()
          const bodyText = (params.bodyText != null ? params.bodyText : '').toString()
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          pres.Slides.AddSlide(position != null ? position : -1)
          let count = typeof pres.Slides.Count === 'number' ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0)
          const newSlide = pres.Slides.Item(count)
          if (newSlide && newSlide.Shapes) {
            if (newSlide.Shapes.Count >= 1 && newSlide.Shapes.Item(1).TextFrame && newSlide.Shapes.Item(1).TextFrame.TextRange) {
              newSlide.Shapes.Item(1).TextFrame.TextRange.Text = titleText
            }
            if (newSlide.Shapes.Count >= 2 && newSlide.Shapes.Item(2).TextFrame && newSlide.Shapes.Item(2).TextFrame.TextRange) {
              newSlide.Shapes.Item(2).TextFrame.TextRange.Text = bodyText
            }
          }
          sendRes('成功：已插入新幻灯片。', null)
        } catch (e) {
          sendRes(null, 'ppt_slide_insert 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_delete') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 0
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          let count = typeof pres.Slides.Count === 'number' ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          if (slide && typeof slide.Delete === 'function') {
            slide.Delete()
            sendRes('成功：已删除该幻灯片。', null)
          } else {
            sendRes(null, '当前 WPS 版本不支持删除幻灯片。')
          }
        } catch (e) {
          sendRes(null, 'ppt_slide_delete 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_document_create') {
        try {
          const filePath = (params.filePath != null ? String(params.filePath) : '').trim()
          if (!filePath) {
            sendRes('[错误] 请提供 filePath（.pptx 或 .pptm）。', null)
            return
          }
          const lower = filePath.toLowerCase()
          if (!lower.endsWith('.pptx') && !lower.endsWith('.pptm')) {
            sendRes('[错误] filePath 须为 .pptx 或 .pptm。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.Presentations.Add()
          if (pres && typeof pres.SaveAs === 'function') {
            pres.SaveAs(filePath)
          }
          sendRes('成功：已新建演示文稿并保存到：' + filePath, null)
        } catch (e) {
          sendRes(null, 'ppt_document_create 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_image_add') {
        try {
          const slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1
          let imagePath = (params.imagePath != null ? String(params.imagePath) : '').trim()
          const imageBase64 = params.imageBase64 != null ? String(params.imageBase64) : ''
          let tempFromB64 = null
          if (!imagePath && imageBase64) {
            try {
              const fso = new ActiveXObject('Scripting.FileSystemObject')
              const folder = fso.GetSpecialFolder(2)
              tempFromB64 = folder + '\\taskly_ppt_img_' + Date.now() + '.bin'
              const stream = new ActiveXObject('ADODB.Stream')
              stream.Type = 1
              stream.Open()
              const xml = new ActiveXObject('Microsoft.XMLDOM')
              const el = xml.createElement('tmp')
              el.dataType = 'bin.base64'
              el.text = imageBase64.replace(/\s/g, '')
              stream.Write(el.nodeTypedValue)
              stream.SaveToFile(tempFromB64, 2)
              stream.Close()
              imagePath = tempFromB64
            } catch (e2) {
              sendRes(
                null,
                'ppt_slide_image_add：无法将 imageBase64 写入临时文件（' +
                  (e2 && e2.message ? e2.message : String(e2)) +
                  '）。请提供本机 imagePath，或使用 Chrome + 后端文件路径。'
              )
              return
            }
          }
          if (!imagePath) {
            sendRes(null, '[错误] 请提供 imagePath，或由服务端随 RPC 附带 imageBase64。')
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex < 1 || slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          const left = 20
          const top = 120
          const width = 400
          const height = 225
          if (slide && slide.Shapes && typeof slide.Shapes.AddPicture === 'function') {
            slide.Shapes.AddPicture(imagePath, false, true, left, top, width, height)
            sendRes('成功：已在第 ' + slideIndex + ' 页插入图片。', null)
          } else {
            sendRes(null, '当前 WPS 版本不支持 Shapes.AddPicture。')
          }
          if (tempFromB64) {
            try {
              new ActiveXObject('Scripting.FileSystemObject').DeleteFile(tempFromB64, true)
            } catch (_) {
              /* ignore */
            }
          }
        } catch (e) {
          sendRes(null, 'ppt_slide_image_add 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_notes_read') {
        try {
          const slideIndex = (params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1) || 1
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          let np = slide.NotesPage
          if (!np && typeof slide.AddNotesPage === 'function') {
            slide.AddNotesPage()
            np = slide.NotesPage
          }
          if (!np) {
            sendRes('（无备注）', null)
            return
          }
          const text = wpsPptNotesPlainText(np)
          sendRes(text ? text : '（无备注）', null)
        } catch (e) {
          sendRes(null, 'ppt_notes_read 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_notes_write') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 0
          const text = (params.text != null ? params.text : '').toString()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          let np = slide.NotesPage
          if (!np && typeof slide.AddNotesPage === 'function') {
            slide.AddNotesPage()
            np = slide.NotesPage
          }
          if (!np || !np.Shapes) {
            sendRes(null, '[错误] 无法创建或访问备注页。')
            return
          }
          wpsPptSetNotesPlainText(np, text)
          sendRes('成功：已写入备注。', null)
        } catch (e) {
          sendRes(null, 'ppt_notes_write 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slides_reorder') {
        try {
          const newOrder = (params.newOrder != null ? String(params.newOrder) : '').trim()
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const n = wpsPptCollectionCount(pres.Slides)
          const parsed = parsePptReorder1Based(newOrder, n)
          if (parsed.err) {
            sendRes(parsed.err, null)
            return
          }
          const order = parsed.order
          const slides = pres.Slides
          const refs = []
          for (let k = 0; k < order.length; k++) {
            refs.push(slides.Item(order[k]))
          }
          for (let targetPos = 1; targetPos <= n; targetPos++) {
            const s = refs[targetPos - 1]
            if (s && typeof s.MoveTo === 'function') {
              s.MoveTo(targetPos)
            } else {
              sendRes(null, '当前 WPS 版本不支持 Slide.MoveTo，无法重排幻灯片。')
              return
            }
          }
          sendRes('成功：已重排幻灯片。', null)
        } catch (e) {
          sendRes(null, 'ppt_slides_reorder 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_table_create') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          const rows = params.rows != null ? parseInt(params.rows, 10) : 2
          const cols = params.cols != null ? parseInt(params.cols, 10) : 2
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          if (rows < 1 || cols < 1 || rows > 20 || cols > 10) {
            sendRes('[错误] 表格行列无效（行 1–20，列 1–10）。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          if (!slide || !slide.Shapes || typeof slide.Shapes.AddTable !== 'function') {
            sendRes(null, '当前 WPS 版本不支持 Shapes.AddTable。')
            return
          }
          const left = 36
          const top = 216
          const width = 720
          const height = 200
          slide.Shapes.AddTable(rows, cols, left, top, width, height)
          sendRes('成功：已在第 ' + slideIndex + ' 页添加表格（' + rows + '×' + cols + '）。', null)
        } catch (e) {
          sendRes(null, 'ppt_table_create 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_table_write_cells') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          const rowsCsv = (params.rowsCsv != null ? params.rowsCsv : '').toString()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const parsedCsv = parsePptRowsCsv(rowsCsv)
          if (parsedCsv.err) {
            sendRes(parsedCsv.err, null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          const tableShape = wpsPptFindFirstTableShape(slide)
          if (!tableShape || !tableShape.Table) {
            sendRes(null, '[错误] 该幻灯片上未找到表格。')
            return
          }
          const tbl = tableShape.Table
          const inputRows = parsedCsv.rows
          for (let r = 0; r < inputRows.length; r++) {
            const rowIdx = r + 1
            const cells = inputRows[r]
            for (let c = 0; c < cells.length; c++) {
              const colIdx = c + 1
              try {
                const cell = tbl.Cell(rowIdx, colIdx)
                if (cell && cell.Shape && cell.Shape.TextFrame && cell.Shape.TextFrame.TextRange) {
                  cell.Shape.TextFrame.TextRange.Text = cells[c]
                }
              } catch (_) {
                /* 越界则跳过 */
              }
            }
          }
          sendRes('成功：已写入表格单元格。', null)
        } catch (e) {
          sendRes(null, 'ppt_table_write_cells 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_hyperlink_add') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          const url = (params.url != null ? String(params.url) : '').trim()
          const shapeIndex = params.shapeIndex != null ? parseInt(params.shapeIndex, 10) : 1
          const shapeName = (params.shapeName != null ? params.shapeName : '').toString().trim()
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          if (!url) {
            sendRes('[错误] URL 为空。', null)
            return
          }
          if (!/^https?:\/\//i.test(url)) {
            sendRes('[错误] URL 必须是绝对地址（如 https://...）。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          const shape = wpsPptPickTextShape(slide, shapeIndex, shapeName)
          if (!shape || !shape.TextFrame || !shape.TextFrame.TextRange) {
            sendRes(null, '[错误] 未找到可设置超链接的文本形状，请检查 shapeIndex 或 shapeName。')
            return
          }
          const clickEnum = app.Enum && app.Enum.ppMouseClick != null ? app.Enum.ppMouseClick : 1
          const aset = shape.ActionSettings(clickEnum)
          if (aset && aset.Hyperlink) {
            aset.Hyperlink.Address = url
            sendRes('成功：已添加超链接。', null)
          } else {
            sendRes(null, '[错误] 无法设置 ActionSettings.Hyperlink。')
          }
        } catch (e) {
          sendRes(null, 'ppt_hyperlink_add 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_duplicate') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          if (slideIndex < 1) {
            sendRes('[错误] slideIndex 必须大于等于 1。', null)
            return
          }
          const app = window.wps.Application || window.wps
          const pres = app.ActivePresentation
          if (!pres || !pres.Slides) {
            sendRes(null, '当前不是 WPS 演示文档。')
            return
          }
          const count = wpsPptCollectionCount(pres.Slides)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          if (slide && typeof slide.Duplicate === 'function') {
            slide.Duplicate()
            sendRes('成功：已复制幻灯片（插入在源页之后）。', null)
          } else {
            sendRes(null, '当前 WPS 版本不支持 Slide.Duplicate。')
          }
        } catch (e) {
          sendRes(null, 'ppt_slide_duplicate 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else {
        sendRes(null, 'Method not supported in this client: ' + method)
      }
    } catch (err) {
      console.error('RPC Error:', err)
      sendRes(null, err.message || String(err))
    }
  }

  // confirm_request：字段与展示逻辑对齐 chrome-extension/sidepanel.js handleConfirmRequest（规范源在 Chrome）
  function handleConfirmRequest(msg) {
    const requestId = msg.id || msg.requestId
    const action = msg.content || msg.action || '未知操作'
    const humanSummary = (msg.humanSummary && String(msg.humanSummary).trim()) || ''
    const hitlKind = msg.hitlKind
    if (!requestId) return
    pendingConfirmId.value = requestId
    hitlHumanSummary.value = humanSummary
    hitlAction.value = action
    hitlShowAddToList.value = hitlKind === 'run_command' || hitlKind === 'run_builtin_page_script'
    hitlVisible.value = true
  }

  function sendConfirmResponse(allowed, addToAllowList = false) {
    const id = pendingConfirmId.value
    if (!id) return
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'confirm_response', id, allowed, addToAllowList: !!addToAllowList }))
    }
    pendingConfirmId.value = null
    // 关闭时清空 HITL 状态：语义对齐 chrome-extension/sidepanel.js sendConfirmResponse（规范源在 Chrome，勿以本 composable 为其它端的参考）
    hitlHumanSummary.value = ''
    hitlVisible.value = false
  }

  // ───── @ 模式（工具/技能选择，与 chrome-extension 侧行为对齐）─────
  const inputAreaRef = ref(null)
  const atModeListRef = ref(null)
  const atModeOpen = ref(false)
  const atModeActiveIndex = ref(0)
  const atTokenStart = ref(-1)
  const atTokenEnd = ref(-1)
  const atModeCandidates = ref([])
  const atModeTopList = ref([])
  const atModeLoaded = ref(false)
  const atModeLoadError = ref('')
  const atModeBootstrapping = ref(false)
  let atModeLoadingPromise = null
  let atModeSyncScheduled = false

  function sanitizeSkillFunctionName(skillId) {
    if (!skillId) return 'Skill'
    let s = String(skillId)
      .trim()
      .replace(/-/g, '_')
      .replace(/\//g, '_')
      .replace(/ /g, '_')
    let out = ''
    let prevUnderscore = false
    for (const c of s) {
      if (/[A-Za-z0-9_]/.test(c)) {
        out += c
        prevUnderscore = false
      } else if (!prevUnderscore) {
        out += '_'
        prevUnderscore = true
      }
    }
    out = out.replace(/^_+|_+$/g, '')
    return out ? out : 'Skill'
  }

  function isWhitespace(ch) {
    return /\s/.test(ch)
  }

  function findAtTokenInTextarea() {
    const el = inputAreaRef.value
    if (!el) return null
    const value = inputText.value || ''
    const caret = el.selectionStart ?? 0
    const left = caret - 1
    if (left < 0) return null
    let i = left
    let lastAt = -1
    while (i >= 0) {
      const ch = value[i]
      if (isWhitespace(ch)) break
      if (ch === '@') lastAt = i
      i--
    }
    if (lastAt < 0) return null
    return { atIndex: lastAt, caret, filter: value.slice(lastAt + 1, caret) }
  }

  async function loadAtModeCandidates() {
    if (atModeLoaded.value) return
    if (atModeLoadingPromise) return atModeLoadingPromise
    atModeLoadingPromise = (async () => {
      atModeLoadError.value = ''
      try {
        await ensureApiBase()
        const [builtinRes, skillsRes] = await Promise.all([
          tasklyFetch(`${API_BASE}/api/tools/builtin`),
          tasklyFetch(`${API_BASE}/api/skills`),
        ])
        const loadErrs = []
        if (!builtinRes.ok) loadErrs.push('内置工具接口 HTTP ' + builtinRes.status)
        if (!skillsRes.ok) loadErrs.push('技能接口 HTTP ' + skillsRes.status)
        const builtins = builtinRes.ok ? await builtinRes.json() : []
        const skills = skillsRes.ok ? await skillsRes.json() : []

        const builtinCandidates = (Array.isArray(builtins) ? builtins : [])
          .map((t) => ({
            group: 'Tools',
            label: t.name || t.id || '',
            internal: t.id || t.Id || '',
            desc: t.description || t.Description || '',
          }))
          .filter((c) => c.internal)

        const skillCandidates = (Array.isArray(skills) ? skills : [])
          .filter((s) => (s.enabled !== false && s.Enabled !== false) && (s.promptTemplate || s.PromptTemplate || ''))
          .map((s) => {
            const id = s.id || s.Id || ''
            const safeName = sanitizeSkillFunctionName(id)
            return {
              group: 'Skills',
              label: s.name || s.Name || id,
              internal: 'UserSkill_' + safeName,
              desc: s.description || s.Description || '',
            }
          })

        builtinCandidates.sort((a, b) => String(a.label).localeCompare(String(b.label), 'zh-Hans'))
        skillCandidates.sort((a, b) => String(a.label).localeCompare(String(b.label), 'zh-Hans'))
        const merged = [...builtinCandidates, ...skillCandidates]
        atModeCandidates.value = merged
        atModeLoaded.value = true
        if (loadErrs.length) {
          atModeLoadError.value =
            '部分数据加载失败：' +
            loadErrs.join('；') +
            '。请确认本机后台已启动（' +
            API_BASE +
            '）。' +
            (merged.length ? ' 以下为已成功加载的条目。' : '')
        }
        if (!merged.length) {
          atModeLoadError.value =
            (atModeLoadError.value ? atModeLoadError.value + ' ' : '') + '当前没有可用的工具或技能可选。'
        }
      } catch (e) {
        console.warn('Failed to load @ mode candidates', e)
        atModeCandidates.value = []
        atModeLoaded.value = true
        atModeLoadError.value =
          '无法加载工具/技能列表：' +
          (e && e.message ? e.message : String(e)) +
          '。请确认本机后台已启动。'
      } finally {
        atModeLoadingPromise = null
      }
    })()
    return atModeLoadingPromise
  }

  function rebuildAtModeList(filterRaw) {
    const filter = (filterRaw || '').trim().toLowerCase()
    const list = atModeCandidates.value || []
    if (!list.length) {
      atModeTopList.value = []
      atModeActiveIndex.value = 0
      return
    }
    const scored = []
    for (const c of list) {
      const label = String(c.label || '').toLowerCase()
      const internal = String(c.internal || '').toLowerCase()
      const text = `${label} ${internal}`
      if (filter && !text.includes(filter)) continue
      let score = 0
      if (!filter) score = 1
      else if (label.startsWith(filter) || internal.startsWith(filter)) score = 100
      else if (label.includes(filter) || internal.includes(filter)) score = 50
      scored.push({ c, score })
    }
    scored.sort((a, b) => {
      const g1 = a.c.group === 'Skills' ? 0 : 1
      const g2 = b.c.group === 'Skills' ? 0 : 1
      if (g1 !== g2) return g1 - g2
      if (b.score !== a.score) return b.score - a.score
      return String(a.c.label).localeCompare(String(b.c.label), 'zh-Hans')
    })
    atModeTopList.value = scored.map((x) => x.c)
    atModeActiveIndex.value = 0
  }

  function openAtModePanel(startIdx, endIdx) {
    atModeOpen.value = true
    atTokenStart.value = startIdx
    atTokenEnd.value = endIdx
    atModeActiveIndex.value = 0
    nextTick(() => scrollAtModeActiveItemIntoView())
  }

  function closeAtMode() {
    atModeOpen.value = false
    atModeActiveIndex.value = 0
    atTokenStart.value = -1
    atTokenEnd.value = -1
    atModeTopList.value = []
  }

  function scrollAtModeActiveItemIntoView() {
    const root = atModeListRef.value
    if (!root) return
    const el = root.querySelector('.at-mode-item--active')
    if (el && typeof el.scrollIntoView === 'function') {
      el.scrollIntoView({ block: 'nearest', inline: 'nearest' })
    }
  }

  function setAtActiveIndex(idx) {
    const n = atModeTopList.value.length
    if (!n) return
    atModeActiveIndex.value = Math.max(0, Math.min(n - 1, idx))
    nextTick(() => scrollAtModeActiveItemIntoView())
  }

  function insertAtCandidate(candidate) {
    if (!candidate || atTokenStart.value < 0 || atTokenEnd.value < 0) return
    const value = inputText.value || ''
    const internal = candidate.internal || ''
    const inserted = `[TOOL:${internal}]`
    const afterChar = value[atTokenEnd.value] || ''
    const trailing = afterChar && !/\s/.test(afterChar) ? ' ' : ''
    const newValue = value.slice(0, atTokenStart.value) + inserted + trailing + value.slice(atTokenEnd.value)
    inputText.value = newValue
    const newCaret = atTokenStart.value + inserted.length + trailing.length
    closeAtMode()
    nextTick(() => {
      const ta = inputAreaRef.value
      if (ta) {
        ta.focus()
        ta.setSelectionRange(newCaret, newCaret)
      }
    })
  }

  function pickAtModeActive() {
    const list = atModeTopList.value
    const c = list[atModeActiveIndex.value]
    if (c) insertAtCandidate(c)
  }

  async function updateAtModeFromTextarea() {
    const el = inputAreaRef.value
    if (!el) return
    const token = findAtTokenInTextarea()
    if (!token) {
      if (atModeOpen.value) closeAtMode()
      return
    }
    const value = inputText.value || ''
    const prev = token.atIndex > 0 ? value[token.atIndex - 1] : ''
    const allow = token.atIndex === 0 || !/[A-Za-z0-9_]/.test(prev)
    if (!allow) {
      if (atModeOpen.value) closeAtMode()
      return
    }
    if (!atModeLoaded.value) {
      atModeBootstrapping.value = true
      try {
        await loadAtModeCandidates()
      } finally {
        atModeBootstrapping.value = false
      }
    }
    const filter = token.filter || ''
    if (
      atModeOpen.value &&
      atTokenStart.value === token.atIndex &&
      atTokenEnd.value === token.caret
    ) {
      return
    }
    rebuildAtModeList(filter)
    if (!atModeBootstrapping.value && atModeTopList.value.length === 0) {
      if (atModeOpen.value) closeAtMode()
      return
    }
    openAtModePanel(token.atIndex, token.caret)
  }

  function scheduleAtModeSync() {
    if (atModeSyncScheduled) return
    atModeSyncScheduled = true
    queueMicrotask(() => {
      atModeSyncScheduled = false
      void updateAtModeFromTextarea()
    })
  }

  function onChatKeydown(e) {
    if (atModeOpen.value) {
      if (e.key === 'Escape') {
        e.preventDefault()
        closeAtMode()
        return
      }
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        setAtActiveIndex(atModeActiveIndex.value + 1)
        return
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault()
        setAtActiveIndex(atModeActiveIndex.value - 1)
        return
      }
      if ((e.key === 'Enter' && !e.shiftKey) || e.key === 'Tab') {
        e.preventDefault()
        pickAtModeActive()
        return
      }
    }
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSend()
    }
  }

  function onChatKeyup() {
    void updateAtModeFromTextarea()
  }

  function onChatInput() {
    scheduleAtModeSync()
  }

  const atModeListPlaceholder = computed(() => {
    if (atModeBootstrapping.value) return '正在加载工具/技能列表…'
    if (!atModeCandidates.value.length) {
      return atModeLoadError.value || '暂无可用工具/技能'
    }
    return ''
  })

  const showWelcome = computed(() => messages.value.length === 0 && !currentRound.value)

  const askOptionsCurrentStep = computed(() => {
    const steps = askOptionsSteps.value
    const idx = askOptionsStepIndex.value
    if (!steps.length || idx < 0 || idx >= steps.length) return null
    return steps[idx]
  })

  const askOptionsStepLabel = computed(() => {
    const n = askOptionsSteps.value.length
    if (!n) return ''
    return `步骤 ${askOptionsStepIndex.value + 1}/${n}`
  })

  onMounted(() => {
    sessionId = getSessionId()
    void loadAgentProfilesFromServer()
      .catch(() => {})
      .finally(() => {
        connect()
      })
  })

  onUnmounted(() => {
    if (reconnectTimer) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    if (ws) {
      ws.close()
      ws = null
    }
  })

  return {
    connected,
    messages,
    currentRound,
    inputText,
    inputEnabled,
    contextUsageRing,
    attachments,
    planPanelVisible,
    planId,
    planTitle,
    planContent,
    planContentEdit,
    planEditMode,
    planChecklistSteps,
    planChecklistStatus,
    planChecklistDoneCount,
    hitlVisible,
    hitlHumanSummary,
    hitlAction,
    hitlShowAddToList,
    askOptionsVisible,
    askOptionsTitle,
    askOptionsPrompt,
    askOptionsCurrentStep,
    askOptionsStepLabel,
    askOptionsSelectedOptionId,
    agentProfileOptions,
    activeAgentProfileId,
    historyOverlayVisible,
    historyItems,
    historyLoading,
    historyHasMore,
    historyError,
    showWelcome,
    loadMoreHistory: () => fetchHistoryPage(true),
    openHistoryOverlay,
    closeHistoryOverlay,
    switchToHistorySession,
    deleteHistorySession,
    setAskOptionsActiveOption,
    confirmAskOptionsStep,
    applyAgentProfileChange,
    handleSend,
    stopStream,
    showPlanEdit,
    cancelPlanEdit,
    cancelPlanBinding,
    resetConversation,
    savePlan,
    executePlan,
    removeAttachment,
    clearAttachments,
    handleFileInputChange,
    sendConfirmResponse,
    escapeHtml,
    marked,
    inputAreaRef,
    atModeListRef,
    atModeOpen,
    atModeActiveIndex,
    atModeTopList,
    atModeListPlaceholder,
    insertAtCandidate,
    onChatKeydown,
    onChatKeyup,
    onChatInput,
    getCopilotSessionId: getSessionId,
    openOfficeCopilotSettingsInChrome
  }
}
