/**
 * Office Copilot 任务窗格逻辑：WebSocket、消息列表、计划面板、RPC、HITL。
 */
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { marked } from 'marked'

const WS_URL = 'ws://localhost:8765/ws'
const API_BASE = 'http://localhost:8765'
const AUTH_TOKEN = 'office-copilot-dev-token'
const RECONNECT_BASE_MS = 1000
const RECONNECT_MAX_MS = 16000
const CLIENT_TYPE = 'wps'

function escapeHtml(unsafe) {
  if (!unsafe) return ''
  return String(unsafe)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;')
}

export function useCopilot() {
  const connected = ref(false)
  const messages = ref([])
  const currentRound = ref(null) // { streamContent: '', toolBlocks: [], isStreaming: true }
  const inputText = ref('')
  const inputEnabled = ref(true)

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
  const hitlAction = ref('')
  const pendingConfirmId = ref(null)
  const hitlShowAddToList = ref(false)

  let ws = null
  let sessionId = null
  let reconnectDelay = RECONNECT_BASE_MS
  let reconnectTimer = null
  let pendingMessages = []
  let currentToolEndIndex = 0

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

  function initPlanChecklistFromContent(content) {
    const steps = parsePlanStepsFromContent(content)
    planChecklistSteps.value = steps
    const nextStatus = {}
    steps.forEach((s) => {
      nextStatus[s.index] = 'pending'
    })
    planChecklistStatus.value = nextStatus
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
    addSystemMessage('已取消当前计划绑定')
  }

  function resetConversation() {
    // Chrome 侧边栏的“新建对话”语义：清空当前对话会话，重连 WS，并清除附件/计划绑定。
    try {
      sessionStorage.removeItem('copilot_session_id')
    } catch {
      /* ignore */
    }
    pendingMessages = []
    currentToolEndIndex = 0
    currentRound.value = null
    messages.value = []
    inputText.value = ''
    inputEnabled.value = true
    clearAttachments()
    cancelPlanBinding()

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
  }

  function beginStream() {
    removeWelcome()
    currentRound.value = {
      streamContent: '',
      toolBlocks: [],
      isStreaming: true
    }
    currentToolEndIndex = 0
    inputEnabled.value = false
  }

  function appendStreamChunk(text) {
    if (!currentRound.value) beginStream()
    currentRound.value.streamContent += text
    currentRound.value.parsedHtml =
      typeof marked.parse === 'function' ? marked.parse(currentRound.value.streamContent) : currentRound.value.streamContent
  }

  function finalizeStream() {
    if (currentRound.value) {
      const round = currentRound.value
      round.isStreaming = false
      round.parsedHtml = typeof marked.parse === 'function' ? marked.parse(round.streamContent) : round.streamContent
      removeWelcome()
      messages.value.push({
        type: 'round',
        streamContent: round.streamContent,
        parsedHtml: round.parsedHtml,
        toolBlocks: [...round.toolBlocks],
        isStreaming: false
      })
      currentRound.value = null
    }
    inputEnabled.value = true
  }

  function setInputEnabled(enabled) {
    inputEnabled.value = enabled
  }

  function connect() {
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return
    sessionId = getSessionId()
    const url =
      WS_URL +
      '?sessionId=' +
      encodeURIComponent(sessionId) +
      '&token=' +
      encodeURIComponent(AUTH_TOKEN) +
      '&clientType=' +
      encodeURIComponent(CLIENT_TYPE)
    ws = new WebSocket(url)
    ws.onopen = () => {
      reconnectDelay = RECONNECT_BASE_MS
      connected.value = true
      addSystemMessage('已连接到本地服务')
      while (pendingMessages.length > 0) {
        const m = pendingMessages.shift()
        send(m.text, m.attachmentsPayload || null)
      }
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
  }

  function scheduleReconnect() {
    if (reconnectTimer) return
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS)
      connect()
    }, reconnectDelay)
  }

  function send(text, attachmentsPayload = null) {
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      addBotMessage('连接已断开，消息未发送。请检查后台是否启动，并在 Chrome 扩展中配置。', true)
      return
    }
    const payload = { type: 'text', content: text || '' }
    if (planId.value) {
      payload.mode = 'agent'
      payload.planId = planId.value
    }
    if (attachmentsPayload && attachmentsPayload.length > 0) {
      payload.attachments = attachmentsPayload
    }
    ws.send(JSON.stringify(payload))
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
        beginStream()
        break
      case 'stream_chunk':
        appendStreamChunk(msg.content)
        break
      case 'stream_end':
        finalizeStream()
        break
      case 'tool_invocation_start': {
        if (!currentRound.value) break
        const label = msg.summary || '正在执行: ' + (msg.plugin || '') + '.' + (msg.function || '')
        currentRound.value.toolBlocks.push({
          label,
          status: 'running',
          output: ''
        })
        if (msg.planStepIndex) {
          updateChecklistStep(msg.planStepIndex, 'in_progress')
        }
        break
      }
      case 'tool_invocation_end': {
        if (!currentRound.value || !currentRound.value.toolBlocks.length) break
        const block = currentRound.value.toolBlocks[currentToolEndIndex]
        if (block) {
          block.status = msg.success === true ? 'done' : 'fail'
          block.output = (msg.content && String(msg.content).trim()) || ''
          block.displayLabel = (block.label || '').replace(/^正在执行:\s*/i, '')
        }
        if (msg.planStepIndex) {
          updateChecklistStep(msg.planStepIndex, msg.success === true ? 'done' : 'fail')
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
      case 'plan_created':
      case 'plan_updated': {
        const planIdMsg = msg.planId || ''
        const title = msg.title || '新计划'
        const createdBy = (msg.createdBy || '').toLowerCase()
        if (planIdMsg && createdBy === CLIENT_TYPE) fetchPlanAndShow(planIdMsg, title, createdBy)
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
    fetch(API_BASE + '/api/plans/' + encodeURIComponent(id))
      .then((res) => res.json().catch(() => ({})).then((data) => {
        if (!res.ok) return Promise.reject(new Error((data && data.message) || '请求失败 ' + res.status))
        return data
      }))
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
    fetch(API_BASE + '/api/plans/' + encodeURIComponent(planId.value), {
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
  }

  function executePlan() {
    if (planId.value) send('请按当前绑定的计划执行')
  }

  const DOCUMENT_SCRIPTS = {
    word_read_selection() {
      if (!window.wps || !window.wps.ActiveDocument) return Promise.resolve('当前环境不可用。')
      try {
        const sel = window.wps.Selection
        const t = sel && sel.Text ? sel.Text : ''
        return Promise.resolve(t || '(无选区)')
      } catch (e) {
        return Promise.resolve('WPS 暂不支持读取选区，请使用 word_read_body。')
      }
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
      if (method === 'word_insert_text') {
        const text = params.text != null ? String(params.text) : ''
        if (window.wps.Enum && window.wps.Enum.wdStory) {
          const doc = window.wps.ActiveDocument
          if (doc && doc.Content) {
            doc.Content.InsertAfter(text)
            sendRes('成功：已在当前 WPS 文档末尾插入内容。', null)
          } else {
            sendRes(null, '无法获取当前文档内容对象。')
          }
        } else {
          sendRes(null, 'WPS 文字 API 需在 WPS 文字加载项中调用，请参考 WPS 开放平台文档。')
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
        sendRes(null, 'WPS 暂不支持插入表格，请根据 WPS 开放平台 API 实现。')
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
      } else if (method === 'excel_read_range' || method === 'excel_write_range' || method === 'excel_list_sheets' || method === 'excel_get_used_range' || method === 'excel_read_formulas' || method === 'excel_write_formulas') {
        sendRes(null, 'WPS 表格 RPC 需根据 WPS 开放平台 API 实现。')
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
          if (slide && slide.Shapes && slide.Shapes.Count > 0) {
            for (let i = 0; i < slide.Shapes.Count; i++) {
              const sh = slide.Shapes.Item(i + 1)
              if (sh && sh.TextFrame && sh.TextFrame.TextRange && sh.TextFrame.TextRange.Text) {
                parts.push(String(sh.TextFrame.TextRange.Text))
              }
            }
          }
          const text = '[幻灯片 ' + slideIndex + ']\n' + (parts.length > 0 ? parts.join(' ').trim() : '(无文本)')
          sendRes(text, null)
        } catch (e) {
          sendRes(null, 'ppt_slide_read 失败：' + (e && e.message ? e.message : String(e)))
        }
      } else if (method === 'ppt_slide_write') {
        try {
          const slideIndex = params.slideIndex != null ? parseInt(params.slideIndex, 10) : 1
          const placeholderType = (params.placeholderType || 'title').toString().trim().toLowerCase()
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
          let count = typeof pres.Slides.Count === 'number' ? pres.Slides.Count : (pres.Slides.Count && pres.Slides.Count.value !== undefined ? pres.Slides.Count.value : 0)
          if (slideIndex > count) {
            sendRes('[错误] 幻灯片序号超出范围。', null)
            return
          }
          const slide = pres.Slides.Item(slideIndex)
          if (!slide || !slide.Shapes || slide.Shapes.Count < 1) {
            sendRes(null, '[错误] 未找到可写入的占位符。')
            return
          }
          const idx = placeholderType === 'body' ? 2 : 1
          const shape = slide.Shapes.Item(idx) || slide.Shapes.Item(1)
          if (!shape || !shape.TextFrame || !shape.TextFrame.TextRange) {
            sendRes(null, '[错误] 未找到可写入的占位符。')
            return
          }
          shape.TextFrame.TextRange.Text = text
          sendRes('成功：已写入幻灯片占位符。', null)
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
      } else {
        sendRes(null, 'Method not supported in this client: ' + method)
      }
    } catch (err) {
      console.error('RPC Error:', err)
      sendRes(null, err.message || String(err))
    }
  }

  function handleConfirmRequest(msg) {
    const requestId = msg.id || msg.requestId
    const action = msg.content || msg.action || '未知操作'
    const hitlKind = msg.hitlKind
    if (!requestId) return
    pendingConfirmId.value = requestId
    hitlAction.value = action
    hitlShowAddToList.value = hitlKind === 'run_command' || hitlKind === 'run_page_script'
    hitlVisible.value = true
  }

  function sendConfirmResponse(allowed, addToAllowList = false) {
    const id = pendingConfirmId.value
    if (!id) return
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'confirm_response', id, allowed, addToAllowList: !!addToAllowList }))
    }
    pendingConfirmId.value = null
    hitlVisible.value = false
  }

  const showWelcome = computed(() => messages.value.length === 0 && !currentRound.value)

  onMounted(() => {
    sessionId = getSessionId()
    connect()
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
    hitlAction,
    hitlShowAddToList,
    showWelcome,
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
    marked
  }
}
