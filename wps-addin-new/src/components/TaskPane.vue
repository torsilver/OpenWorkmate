<template>
  <div class="copilot-app">
    <header class="header">
      <div class="header-title">
        <span class="header-icon">⚡</span>
        <h1>Office Copilot</h1>
      </div>
      <div class="header-controls">
        <button type="button" class="header-btn" title="新建对话" @click="resetConversation">
          💬
        </button>
        <div
          :class="['status', connected ? 'status--connected' : 'status--disconnected']"
          title="连接状态"
        >
          <span class="status-dot"></span>
          <span class="status-text">{{ connected ? '已连接' : '未连接' }}</span>
        </div>
      </div>
    </header>
    <p class="config-hint">
      本机后台与 AI 的<strong>完整设置</strong>请仅在 <strong>Chrome 扩展</strong> 的选项页完成并保存（默认 <span class="config-hint-link">127.0.0.1:8765</span> 起若端口被占用会自动顺延，扩展会自动发现）。访问密钥会在本机首次连接时自动从后台同步到当前源；若仍无法连接，可在控制台手动 <code>localStorage.setItem('tasklyLocalServiceAuthToken','密钥')</code> 后刷新。
    </p>

    <div v-if="planChecklistSteps && planChecklistSteps.length > 0" class="plan-checklist-wrap">
      <details class="plan-checklist-details" open>
        <summary class="plan-checklist-summary">
          执行进度 ({{ planChecklistDoneCount }}/{{ planChecklistSteps.length }})
        </summary>
        <ul class="plan-checklist-list">
          <li
            v-for="s in planChecklistSteps"
            :key="s.index"
            class="plan-step"
            :class="'plan-step--' + (planChecklistStatus[s.index] || 'pending')"
          >
            <span class="plan-step-icon">
              {{
                (planChecklistStatus[s.index] || 'pending') === 'done'
                  ? '✓'
                  : (planChecklistStatus[s.index] || 'pending') === 'in_progress'
                    ? '◐'
                    : '○'
              }}
            </span>
            <span class="plan-step-title">步骤 {{ s.index }}: {{ s.title }}</span>
          </li>
        </ul>
      </details>
    </div>

    <div v-show="planPanelVisible" class="plan-panel">
      <div class="plan-panel-header">
        <span class="plan-panel-title">{{ planTitle }}</span>
        <div class="plan-panel-actions">
          <button type="button" class="btn-execute-plan" @click="executePlan">执行计划</button>
          <button v-show="!planEditMode" type="button" class="btn-edit-plan" @click="showPlanEdit">编辑</button>
          <button v-show="planEditMode" type="button" class="btn-save-plan" @click="savePlan">保存</button>
          <button v-show="planEditMode" type="button" class="btn-cancel-edit" @click="cancelPlanEdit">取消</button>
        </div>
      </div>
      <div v-show="!planEditMode" class="plan-content-view markdown-body" v-html="planContentRendered"></div>
      <textarea
        v-show="planEditMode"
        v-model="planContentEdit"
        class="plan-content-edit"
        rows="10"
      ></textarea>
    </div>

    <main ref="messagesRef" class="messages">
      <div v-if="showWelcome" class="welcome">
        <p class="welcome-title">你好，我是 Office Copilot 👋</p>
        <p class="welcome-sub">在此与 AI 对话，可操作当前 WPS 文档。模型与密钥等请在 Chrome 扩展选项页配置。</p>
      </div>
      <template v-for="(msg, idx) in messages" :key="idx">
        <div v-if="msg.type === 'system'" class="msg msg--system">{{ msg.content }}</div>
        <div v-else-if="msg.type === 'user'" class="msg msg--user">{{ msg.content }}</div>
        <div v-else-if="msg.type === 'bot'" :class="['msg', 'msg--bot', msg.isError ? 'msg--error' : '']">
          <div v-html="msg.html"></div>
        </div>
        <div v-else-if="msg.type === 'round'" class="msg msg--round">
          <div
            v-for="(w, wi) in msg.streamWarnings || []"
            :key="'sw-' + idx + '-' + wi"
            class="msg msg--stream-warning"
          >
            {{ w }}
          </div>
          <div v-if="msg.timelineSegments && msg.timelineSegments.length" class="msg--agent-timeline">
            <template v-for="seg in msg.timelineSegments" :key="seg.id">
              <details
                v-if="seg.kind !== 'tool'"
                class="timeline-seg"
                :class="'timeline-seg--' + seg.kind"
                :open="false"
              >
                <summary>
                  <span class="timeline-seg__label">{{ seg.title }}</span>
                  <span class="timeline-seg__tail">{{ seg.tail }}</span>
                </summary>
                <pre v-if="seg.kind !== 'answer'" class="timeline-seg__body">{{ seg.body }}</pre>
                <div
                  v-else
                  class="timeline-seg__body timeline-seg__body--md markdown-body"
                  v-html="seg.parsedHtml || escapeHtml(seg.body)"
                ></div>
              </details>
              <details
                v-else
                :class="['tool-call-block', 'tool-call--' + seg.status]"
                :open="false"
              >
                <summary>
                  <span class="tool-status-icon">{{ seg.status === 'running' ? '⏳' : seg.status === 'done' ? '✓' : '✗' }}</span>
                  {{ escapeHtml(seg.displayLabel || seg.label) }}
                </summary>
                <pre v-show="seg.output" class="tool-call-output">{{ seg.output }}</pre>
              </details>
            </template>
          </div>
          <div
            v-if="roundNeedsBottomBubble(msg)"
            :class="['msg', 'msg--bot', msg.isStreaming ? 'msg--streaming' : '']"
            v-html="msg.parsedHtml"
          ></div>
        </div>
      </template>
      <!-- 当前正在进行的 round -->
      <div v-if="currentRound" class="msg msg--round">
        <div
          v-for="(w, wi) in currentRound.streamWarnings || []"
          :key="'csw-' + wi"
          class="msg msg--stream-warning"
        >
          {{ w }}
        </div>
        <div v-if="currentRound.timelineSegments && currentRound.timelineSegments.length" class="msg--agent-timeline">
          <template v-for="seg in currentRound.timelineSegments" :key="seg.id">
            <details
              v-if="seg.kind !== 'tool'"
              class="timeline-seg"
              :class="'timeline-seg--' + seg.kind"
              :open="seg.open"
            >
              <summary>
                <span class="timeline-seg__label">{{ seg.title }}</span>
                <span class="timeline-seg__tail">{{ seg.tail }}</span>
              </summary>
              <pre v-if="seg.kind !== 'answer'" class="timeline-seg__body">{{ seg.body }}</pre>
              <div
                v-else
                class="timeline-seg__body timeline-seg__body--md markdown-body"
                v-html="seg.parsedHtml || escapeHtml(seg.body)"
              ></div>
            </details>
            <details
              v-else
              :class="['tool-call-block', 'tool-call--' + seg.status]"
              :open="seg.status === 'running'"
            >
              <summary>
                <span class="tool-status-icon">{{ seg.status === 'running' ? '⏳' : seg.status === 'done' ? '✓' : '✗' }}</span>
                {{ escapeHtml(seg.displayLabel || seg.label) }}
              </summary>
              <pre v-show="seg.output" class="tool-call-output">{{ seg.output }}</pre>
            </details>
          </template>
        </div>
      </div>
    </main>

    <footer class="input-area">
      <div class="current-plan-bar">
        <span class="current-plan-label">当前计划：</span>
        <span
          class="current-plan-value"
          :title="planId ? ('点击复制计划：' + (planTitle || planId)) : '无'"
          @click="copyCurrentPlan"
        >
          {{ planId ? (planTitle || planId) : '无' }}
        </span>
        <button
          v-show="planId"
          type="button"
          class="btn-cancel-plan"
          title="取消当前计划绑定"
          @click="cancelPlanBinding"
        >
          ✕
        </button>
      </div>

      <div v-if="attachments && attachments.length > 0" id="attachments-preview" class="attachments-preview">
        <div v-for="att in attachments" :key="att.id" class="attachment-thumb-wrap">
          <img
            class="attachment-thumb"
            :src="'data:' + (att.mimeType || 'image/png') + ';base64,' + att.data"
            alt="附件"
          />
          <button type="button" class="attachment-remove" title="移除" @click="removeAttachment(att.id)">
            ×
          </button>
        </div>
      </div>

      <div class="input-row">
        <input
          type="file"
          id="file-input"
          accept="image/*"
          multiple
          hidden
          @change="handleFileInputChange"
          ref="fileInputRef"
        />
        <button
          id="attach-btn"
          class="attach-btn"
          type="button"
          title="附加图片"
          @click="triggerAttach"
        >
          +
        </button>

        <textarea
          ref="inputAreaRef"
          v-model="inputText"
          class="input-box"
          placeholder="输入消息…（@ 可选工具/技能，@ 后用关键字过滤；↑↓ 切换、回车确认）"
          rows="1"
          :disabled="!inputEnabled"
          @keydown="onChatKeydown"
          @keyup="onChatKeyup"
          @input="onChatInput"
        ></textarea>
        <button
          v-show="!inputEnabled"
          type="button"
          class="stop-btn"
          title="停止生成"
          @click="stopStream"
        >■</button>
        <button
          v-show="inputEnabled"
          type="button"
          class="send-btn"
          title="发送"
          :disabled="!inputEnabled"
          @click="handleSend"
        >发送</button>
      </div>

      <div v-show="atModeOpen" class="at-mode-panel" role="dialog" aria-label="工具与技能选择">
        <div ref="atModeListRef" class="at-mode-list" role="listbox" aria-label="工具/技能列表">
          <div v-if="!atModeTopList.length" class="at-mode-empty">{{ atModeListPlaceholder }}</div>
          <template v-else>
            <template v-for="(c, idx) in atModeTopList" :key="(c.internal || '') + '-' + idx">
              <div
                v-if="idx > 0 && c.group === 'Tools' && atModeTopList[idx - 1].group === 'Skills'"
                class="at-mode-separator"
                role="separator"
                aria-hidden="true"
              />
              <div
                class="at-mode-item"
                :class="{ 'at-mode-item--active': idx === atModeActiveIndex }"
                role="option"
                :aria-selected="idx === atModeActiveIndex"
                @mousedown.prevent="insertAtCandidate(c)"
              >
                <div class="at-mode-item-title">{{ c.label || c.internal }}</div>
              </div>
            </template>
          </template>
        </div>
      </div>
    </footer>

    <div
      v-show="hitlVisible"
      class="hitl-overlay"
      aria-hidden="false"
    >
      <div class="hitl-card">
        <p class="hitl-title">AI 请求执行</p>
        <p class="hitl-action">{{ hitlAction }}</p>
        <div class="hitl-buttons">
          <button type="button" class="hitl-btn hitl-btn--allow" @click="sendConfirmResponse(true, false)">允许</button>
          <button
            v-show="hitlShowAddToList"
            type="button"
            class="hitl-btn hitl-btn--allow"
            @click="sendConfirmResponse(true, true)"
          >加入白名单并执行</button>
          <button type="button" class="hitl-btn hitl-btn--deny" @click="sendConfirmResponse(false)">拒绝</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script>
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useCopilot } from '../composables/useCopilot'

export default {
  name: 'TaskPane',
  setup() {
    const copilot = useCopilot()
    const fileInputRef = ref(null)

    // TaskPane 在 WPS 内通常被 iframe 承载。
    // iframe 的可视高度往往小于宿主窗口高度，若组件依赖 100vh/min-height:100vh 会导致右侧溢出出现滚动条。
    // 这里在进入/离开任务窗格时，临时把 html/body/#app 的高度与 padding 改为“跟随 iframe”。
    onMounted(() => {
      const appEl = document.getElementById('app')
      if (appEl) {
        appEl.style.padding = '0'
        appEl.style.maxWidth = 'none'
        appEl.style.margin = '0'
        appEl.style.height = '100%'
      }
      document.documentElement.style.height = '100%'
      document.body.style.height = '100%'
      document.body.style.minHeight = '0'
    })

    onUnmounted(() => {
      // 任务窗格离开时恢复为默认样式（保守起见只清理我们改过的行内样式）。
      const appEl = document.getElementById('app')
      if (appEl) {
        appEl.style.removeProperty('padding')
        appEl.style.removeProperty('max-width')
        appEl.style.removeProperty('margin')
        appEl.style.removeProperty('height')
      }
      document.documentElement.style.removeProperty('height')
      document.body.style.removeProperty('height')
      document.body.style.removeProperty('min-height')
    })

    const planContentRendered = computed(() => {
      const raw = copilot.planContent.value || ''
      return typeof copilot.marked?.parse === 'function' ? copilot.marked.parse(raw) : copilot.escapeHtml(raw)
    })
    function roundNeedsBottomBubble(msg) {
      if (!msg) return false
      const segs = msg.timelineSegments || []
      if (segs.some((s) => s.kind === 'answer')) return false
      const html = msg.parsedHtml && String(msg.parsedHtml).trim()
      const raw = msg.streamContent && String(msg.streamContent).trim()
      return !!(html || raw)
    }
    return {
      ...copilot,
      roundNeedsBottomBubble,
      fileInputRef,
      triggerAttach: () => {
        fileInputRef.value?.click()
      },
      planContentRendered,
      async copyCurrentPlan() {
        const text = copilot.planId.value ? (copilot.planTitle.value || copilot.planId.value) : ''
        if (!text) return
        try {
          await navigator.clipboard.writeText(text)
        } catch (e) {
          // 兜底：无法写剪贴板时，至少给用户可读反馈。
          alert('复制失败，请手动复制：' + text)
        }
      }
    }
  }
}
</script>

<style scoped>
.copilot-app {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  font-family: -apple-system, 'Segoe UI', 'Microsoft YaHei', sans-serif;
  font-size: 14px;
  background: var(--copilot-bg-primary, #0f0f0f);
  color: var(--copilot-text-primary, #e8e8e8);
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--copilot-border, #333);
  background: var(--copilot-bg-secondary, #1a1a1a);
  flex-shrink: 0;
}

.header-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.header-icon {
  font-size: 20px;
}

.header h1 {
  font-size: 15px;
  font-weight: 600;
  letter-spacing: -0.01em;
}

.header-controls {
  display: flex;
  align-items: center;
  gap: 12px;
}

.header-btn {
  background: transparent;
  border: none;
  cursor: pointer;
  color: var(--copilot-text-primary, #e8e8e8);
  font-size: 16px;
  padding: 0 2px;
  flex-shrink: 0;
}

.header-btn:hover {
  color: var(--copilot-accent, #6c8cff);
}

.status {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--copilot-text-secondary, #999);
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--copilot-danger, #f87171);
  transition: background 150ms ease;
}

.status--connected .status-dot {
  background: var(--copilot-success, #4ade80);
}

.config-hint {
  font-size: 11px;
  color: var(--copilot-text-secondary, #999);
  padding: 4px 0;
  flex-shrink: 0;
}

.config-hint-link {
  color: var(--copilot-accent, #6c8cff);
  cursor: pointer;
}

.plan-checklist-wrap {
  flex-shrink: 0;
  padding: 8px 16px;
  border-bottom: 1px solid var(--copilot-border, #333);
  background: var(--copilot-bg-secondary, #1a1a1a);
}

.plan-checklist-details {
  font-size: 12px;
  color: var(--copilot-text-secondary, #999);
}

.plan-checklist-summary {
  cursor: pointer;
  font-weight: 500;
  color: var(--copilot-text-primary, #e8e8e8);
}

.plan-checklist-list {
  list-style: none;
  margin: 8px 0 0 0;
  padding: 0;
}

.plan-step {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 0;
  font-size: 11px;
}

.plan-step-icon {
  flex-shrink: 0;
}

.plan-step--done {
  color: var(--copilot-success, #4ade80);
}

.plan-step--in_progress {
  color: var(--copilot-accent, #6c8cff);
}

.current-plan-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
}

.current-plan-label {
  font-size: 12px;
  color: var(--copilot-text-secondary, #999);
  white-space: nowrap;
}

.current-plan-value {
  font-size: 11px;
  color: var(--copilot-accent, #6c8cff);
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.btn-cancel-plan {
  margin-left: auto;
  background: transparent;
  border: none;
  cursor: pointer;
  font-size: 12px;
  color: var(--copilot-text-secondary, #999);
  padding: 0 2px;
}

.attachments-preview {
  display: flex;
  gap: 8px;
  align-items: center;
  padding: 6px 0 0;
  flex-wrap: wrap;
}

.attachment-thumb-wrap {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  background: var(--copilot-bg-tertiary, #252525);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 10px;
}

.attachment-thumb {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 6px;
}

.attachment-remove {
  width: 20px;
  height: 20px;
  border-radius: 999px;
  border: none;
  cursor: pointer;
  background: transparent;
  color: var(--copilot-text-secondary, #999);
  font-size: 16px;
  line-height: 20px;
}

.attachment-remove:hover {
  color: var(--copilot-danger, #f87171);
}

.attach-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  background: transparent;
  color: var(--copilot-text-primary, #e8e8e8);
  flex-shrink: 0;
}

.attach-btn:hover {
  background: rgba(108, 140, 255, 0.12);
}

.btn-cancel-plan,
.attachment-remove,
.attach-btn:focus {
  outline: none;
}

.plan-panel {
  flex-shrink: 0;
  margin: 0 16px 12px;
  padding: 12px;
  background: var(--copilot-bg-secondary, #1a1a1a);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 12px;
  max-height: 240px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.plan-panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 8px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--copilot-border, #333);
}

.plan-panel-title {
  font-weight: 600;
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.plan-panel-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.plan-panel-actions button {
  padding: 4px 10px;
  font-size: 12px;
  border-radius: 6px;
  border: 1px solid var(--copilot-border, #333);
  background: var(--copilot-bg-tertiary, #252525);
  color: var(--copilot-text-primary, #e8e8e8);
  cursor: pointer;
}

.plan-panel-actions button:hover {
  background: rgba(108, 140, 255, 0.12);
  border-color: var(--copilot-accent, #6c8cff);
  color: var(--copilot-accent, #6c8cff);
}

.plan-content-view {
  flex: 1;
  overflow-y: auto;
  font-size: 12px;
  line-height: 1.5;
  max-height: 160px;
}

.plan-content-edit {
  width: 100%;
  min-height: 120px;
  padding: 8px;
  font-size: 12px;
  background: var(--copilot-bg-primary, #0f0f0f);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 6px;
  color: var(--copilot-text-primary, #e8e8e8);
  resize: vertical;
}

.btn-execute-plan {
  background: var(--copilot-success, #4ade80);
  border-color: var(--copilot-success, #4ade80);
  color: #0f0f0f;
}

.btn-execute-plan:hover {
  filter: brightness(1.1);
}

.messages {
  flex: 1;
  overflow-y: auto;
  min-height: 0;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.welcome {
  text-align: center;
  padding: 48px 16px;
  color: var(--copilot-text-secondary, #999);
}

.welcome-title {
  font-size: 18px;
  font-weight: 600;
  color: var(--copilot-text-primary, #e8e8e8);
  margin-bottom: 8px;
}

.welcome-sub {
  font-size: 13px;
  line-height: 1.5;
}

.msg {
  max-width: 88%;
  padding: 10px 14px;
  border-radius: 12px;
  line-height: 1.55;
  font-size: 13.5px;
  word-break: break-word;
}

.msg--user {
  align-self: flex-end;
  background: var(--copilot-user-bubble, #2a2d5e);
  color: #dce4ff;
  border-bottom-right-radius: 4px;
}

.msg--bot {
  align-self: flex-start;
  background: var(--copilot-bot-bubble, #1e1e1e);
  border: 1px solid var(--copilot-border, #333);
  border-bottom-left-radius: 4px;
}

.msg--system {
  align-self: center;
  font-size: 12px;
  color: var(--copilot-text-secondary, #999);
  background: transparent;
  padding: 4px 0;
}

.msg--round {
  display: flex;
  flex-direction: column;
  gap: 0;
  align-self: flex-start;
  max-width: 100%;
}

.msg--agent-timeline {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 8px;
  align-self: flex-start;
  max-width: 100%;
}

.timeline-seg {
  font-size: 12px;
  background: var(--copilot-bg-tertiary, #252525);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 8px;
  overflow: hidden;
}

.timeline-seg summary {
  padding: 6px 10px;
  cursor: pointer;
  list-style: none;
  user-select: none;
  color: var(--copilot-text-secondary, #999);
}

.timeline-seg summary::-webkit-details-marker {
  display: none;
}

.timeline-seg__label {
  font-weight: 600;
  margin-right: 6px;
}

.timeline-seg__tail {
  opacity: 0.9;
  word-break: break-word;
}

.timeline-seg__body {
  margin: 0;
  padding: 8px 10px;
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 220px;
  overflow-y: auto;
  border-top: 1px solid var(--copilot-border, #333);
  background: var(--copilot-bg-primary, #0f0f0f);
  color: var(--copilot-text-secondary, #999);
}

.timeline-seg__body--md {
  white-space: normal;
  font-size: 12px;
  line-height: 1.45;
  color: var(--copilot-text-primary, #e8e8e8);
  max-height: min(70vh, 480px);
}
.timeline-seg--answer .timeline-seg__body--md :is(p, ul, ol, pre, h1, h2, h3, h4) {
  margin: 0.35em 0;
}
.timeline-seg--answer .timeline-seg__body--md pre {
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 11px;
}

.msg--stream-warning {
  padding: 8px 12px;
  margin-bottom: 6px;
  font-size: 13px;
  border-radius: 8px;
  border: 1px solid rgba(251, 191, 36, 0.5);
  background: rgba(251, 191, 36, 0.12);
  color: #eab308;
  align-self: flex-start;
  max-width: 100%;
}

.msg--agent-activity {
  flex-shrink: 0;
  padding: 6px 10px;
  margin-bottom: 6px;
  font-size: 12px;
  line-height: 1.35;
  color: var(--copilot-text-secondary, #999);
  background: rgba(108, 140, 255, 0.12);
  border: 1px solid rgba(108, 140, 255, 0.25);
  border-radius: 8px;
  white-space: nowrap;
  overflow: hidden;
  direction: ltr;
  align-self: flex-start;
  max-width: 100%;
}

.msg--agent-activity-collapsed {
  margin-bottom: 6px;
  font-size: 12px;
  background: var(--copilot-bg-tertiary, #252525);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 8px;
  overflow: hidden;
  align-self: flex-start;
  max-width: 100%;
}

.msg--agent-activity-collapsed summary {
  padding: 6px 10px;
  cursor: pointer;
  list-style: none;
  user-select: none;
  color: var(--copilot-text-secondary, #999);
}

.msg--agent-activity-collapsed summary::-webkit-details-marker {
  display: none;
}

.agent-activity-full {
  margin: 0;
  padding: 8px 10px;
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 160px;
  overflow-y: auto;
  border-top: 1px solid var(--copilot-border, #333);
  color: var(--copilot-text-secondary, #999);
  background: var(--copilot-bg-primary, #0f0f0f);
}

.msg--error {
  border-color: rgba(248, 113, 113, 0.4);
  background: rgba(248, 113, 113, 0.08);
}

.msg--streaming::after {
  content: '▌';
  color: var(--copilot-accent, #6c8cff);
  animation: blink 0.6s step-end infinite;
}

@keyframes blink {
  50% {
    opacity: 0;
  }
}

.msg--execution-log {
  margin-top: 6px;
  font-size: 12px;
  color: var(--copilot-text-secondary, #999);
  background: var(--copilot-bg-tertiary, #252525);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 12px;
  overflow: hidden;
}

.msg--execution-log summary {
  padding: 6px 10px;
  cursor: pointer;
  list-style: none;
  user-select: none;
}

.execution-log-body {
  padding: 0 8px 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.tool-call-block summary {
  padding: 5px 8px;
  cursor: pointer;
  list-style: none;
}

.tool-call-output {
  margin: 0;
  padding: 8px;
  font-size: 11px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 200px;
  overflow-y: auto;
  border-top: 1px solid var(--copilot-border, #333);
  background: var(--copilot-bg-primary, #0f0f0f);
  color: var(--copilot-text-secondary, #999);
}

.tool-call-block.tool-call--running summary {
  color: var(--copilot-accent, #6c8cff);
}

.tool-call-block.tool-call--done summary {
  color: var(--copilot-success, #4ade80);
}

.tool-call-block.tool-call--fail summary {
  color: var(--copilot-danger, #f87171);
}

.input-area {
  position: relative;
  padding: 12px 16px;
  border-top: 1px solid var(--copilot-border, #333);
  background: var(--copilot-bg-secondary, #1a1a1a);
  flex-shrink: 0;
}

.input-row {
  display: flex;
  align-items: flex-end;
  gap: 8px;
  background: var(--copilot-bg-tertiary, #252525);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 12px;
  padding: 4px 4px 4px 14px;
  position: relative;
}

.input-row:focus-within {
  border-color: var(--copilot-accent, #6c8cff);
}

.input-box {
  flex: 1;
  border: none;
  outline: none;
  background: transparent;
  color: var(--copilot-text-primary, #e8e8e8);
  font-size: 14px;
  font-family: inherit;
  resize: none;
  max-height: 120px;
  line-height: 1.5;
  padding: 8px 0;
}

.input-box::placeholder {
  color: var(--copilot-text-secondary, #999);
}

.at-mode-panel {
  position: absolute;
  left: 0;
  right: 0;
  bottom: calc(100% + 10px);
  z-index: 50;
  background: var(--copilot-bg-primary, #0f0f0f);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 12px;
  padding: 10px;
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.35);
  max-height: 320px;
}

.at-mode-list {
  max-height: 240px;
  overflow-y: auto;
}

.at-mode-separator {
  height: 0;
  margin: 8px 4px;
  border: none;
  border-top: 1px solid var(--copilot-border, #333);
  pointer-events: none;
}

.at-mode-item {
  padding: 8px 10px;
  border-radius: 8px;
  cursor: pointer;
}

.at-mode-item:hover {
  background: var(--copilot-bg-tertiary, #252525);
}

.at-mode-item--active {
  background: rgba(108, 140, 255, 0.15);
  outline: 1px solid var(--copilot-accent, #6c8cff);
}

.at-mode-item-title {
  font-size: 13px;
  color: var(--copilot-text-primary, #e8e8e8);
}

.at-mode-item-meta {
  font-size: 11px;
  color: var(--copilot-text-secondary, #999);
  margin-top: 2px;
  word-break: break-all;
}

.at-mode-empty {
  padding: 12px;
  font-size: 13px;
  color: var(--copilot-text-secondary, #999);
  text-align: center;
}

.send-btn,
.stop-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  flex-shrink: 0;
}

.send-btn {
  background: var(--copilot-accent, #6c8cff);
  color: #fff;
}

.send-btn:disabled {
  opacity: 0.3;
  cursor: not-allowed;
}

.stop-btn {
  background: var(--copilot-danger, #f87171);
  color: #fff;
}

.hitl-overlay {
  position: fixed;
  inset: 0;
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.6);
  padding: 16px;
}

.hitl-card {
  background: var(--copilot-bg-secondary, #1a1a1a);
  border: 1px solid var(--copilot-border, #333);
  border-radius: 12px;
  padding: 20px;
  max-width: 360px;
  width: 100%;
}

.hitl-title {
  font-weight: 600;
  margin-bottom: 8px;
}

.hitl-action {
  color: var(--copilot-text-secondary, #999);
  font-size: 13px;
  margin-bottom: 16px;
  word-break: break-word;
}

.hitl-buttons {
  display: flex;
  gap: 10px;
  justify-content: flex-end;
}

.hitl-btn {
  padding: 8px 16px;
  border-radius: 8px;
  font-size: 13px;
  cursor: pointer;
  border: none;
}

.hitl-btn--allow {
  background: var(--copilot-accent, #6c8cff);
  color: #fff;
}

.hitl-btn--deny {
  background: var(--copilot-bg-tertiary, #252525);
  color: var(--copilot-text-primary, #e8e8e8);
  border: 1px solid var(--copilot-border, #333);
}
</style>

<style>
/* 全局 markdown 内容（v-html）需要非 scoped */
.copilot-app .markdown-body p {
  margin-bottom: 8px;
}

.copilot-app .markdown-body p:last-child {
  margin-bottom: 0;
}

.copilot-app .markdown-body pre {
  background: #0d1117;
  padding: 10px;
  border-radius: 6px;
  overflow-x: auto;
  margin: 8px 0;
  border: 1px solid var(--copilot-border, #333);
}

.copilot-app .mermaid-container {
  margin: 8px 0;
  overflow-x: auto;
}

.copilot-app .markdown-body code {
  font-family: Consolas, Monaco, monospace;
  font-size: 13px;
}

.copilot-app .markdown-body p > code {
  background: rgba(255, 255, 255, 0.1);
  padding: 2px 4px;
  border-radius: 4px;
}

.copilot-app .markdown-body table {
  border-collapse: collapse;
  width: 100%;
  margin: 8px 0;
}

.copilot-app .markdown-body th,
.copilot-app .markdown-body td {
  border: 1px solid var(--copilot-border, #333);
  padding: 6px 10px;
  text-align: left;
}

.copilot-app .markdown-body th {
  background: var(--copilot-bg-tertiary, #252525);
}

.copilot-app .markdown-body ul,
.copilot-app .markdown-body ol {
  margin: 8px 0 8px 20px;
}
</style>
