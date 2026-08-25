<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { computed, nextTick, ref, watch } from 'vue'
import { buildCompactionDisplay, buildCompactionTokenDisplay } from '../compactionPresentation'
import { isMarkdownFile } from '../composables/useFileHandling'
import { isSelfContainedImageRef } from '../markdownAssets'
import { buildConversationTimeline, fileLabel, formatFileSize, toolArgumentsText, toolPresentation } from '../messagePresentation'
import { formatTokenBreakdown, formatTokenCount, formatTokenUsage } from '../tokenUsage'
import type { ContextSummary, ConversationMessage, CurrentUserContext, MessageFile, ProcessActivity, ToolActivity } from '../types'
import MarkdownContent from './MarkdownContent.vue'

const props = defineProps<{
  messages: ConversationMessage[]
  contextSummaries?: ContextSummary[]
  loading: boolean
  currentUser: CurrentUserContext | null
  streaming: boolean
  /** 已解析的 markdown 图片 blob URL（见 useFileHandling.markdownImageUrls）。 */
  markdownImageUrls?: Map<string, string>
}>()

const emit = defineEmits<{
  suggest: [value: string]
  download: [file: MessageFile]
}>()

const suggestions = [
  ['分析一个需求', '帮我拆解这个需求，并给出可执行计划。'],
  ['检查服务状态', '检查当前服务状态和可用 Agent。'],
  ['总结技术方案', '请用简洁的结构总结当前技术方案。'],
]

interface MessagesScrollbar {
  setScrollTop: (value: number) => void
  wrapRef?: HTMLElement | null
}

// 距底部小于该阈值视为“贴着底部”，继续自动跟随新内容。
const BOTTOM_STICK_THRESHOLD = 48
// 向上滚动后的冷却期：期间不允许贴底判定重新接管，避免小幅上滑被抵消。
const REENGAGE_COOLDOWN_MS = 400

const messagesScrollbar = ref<MessagesScrollbar | null>(null)
const stickToBottom = ref(true)
const timelineItems = computed(() => buildConversationTimeline(
  props.messages,
  props.contextSummaries || [],
))
type TimelineRow =
  | (ConversationMessage & { timelineKind: 'message' })
  | { timelineKind: 'summary'; messageId: string; role: 'summary'; timestamp: string; contextSummary: ContextSummary }
const timelineRows = computed<TimelineRow[]>(() => timelineItems.value.map(item => item.kind === 'message'
  ? { ...item.message, timelineKind: 'message' }
  : {
      timelineKind: 'summary',
      messageId: `context-summary-${item.summary.compressionId}`,
      role: 'summary',
      timestamp: item.summary.lastCompressedAt,
      contextSummary: item.summary,
    }))
const displayMessages = computed(() => timelineRows.value
  .filter((item): item is ConversationMessage & { timelineKind: 'message' } => item.timelineKind === 'message'))

function hasMessageContent(message: ConversationMessage): boolean {
  return Boolean(message.content || message.files?.length)
}

function isStreamingItem(message: ConversationMessage): boolean {
  const last = displayMessages.value[displayMessages.value.length - 1]
  return props.streaming && message.role === 'assistant' && last?.messageId === message.messageId
}

/** 思考阶段：消息正在流式生成，且尚未输出正文内容。 */
function isThinking(message: ConversationMessage): boolean {
  return isStreamingItem(message) && !message.content
}

function shouldShowUsage(message: ConversationMessage): boolean {
  return message.role === 'assistant' && !message.toolName && !isStreamingItem(message)
}

function processActivities(message: ConversationMessage): ProcessActivity[] {
  if (message.processActivities?.length) return message.processActivities
  return [
    ...(message.reasoning ? [{ kind: 'reasoning' as const, content: message.reasoning }] : []),
    ...(message.toolActivities || []).map(tool => ({ kind: 'tool' as const, tool })),
  ]
}

function processSummary(message: ConversationMessage): string {
  const activities = processActivities(message)
  const reasoningCount = activities.filter(activity => activity.kind === 'reasoning').length
  const toolCount = activities.length - reasoningCount
  const parts = []
  if (reasoningCount) parts.push(`${reasoningCount} 段思考`)
  if (toolCount) parts.push(`${toolCount} 项操作`)
  return parts.join(' · ')
}

function incompleteResponseText(message: ConversationMessage): string {
  if (message.metadata?.ExecutionStatus === 'Cancelled') return '响应已取消'
  if (message.metadata?.ExecutionStatus === 'Failed') return '响应失败'
  return '响应未完成'
}

function toolResultText(tool: ToolActivity): string | undefined {
  if (tool.result == null) return undefined
  try { return JSON.stringify(JSON.parse(tool.result), null, 2) } catch { return tool.result }
}

function toolStatusText(tool: ToolActivity, streaming: boolean): string {
  if (tool.result != null) return '已完成'
  return streaming ? '运行中' : '已调用'
}

function formatTimestamp(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

function compactionDisplay(summary: ContextSummary) {
  return buildCompactionDisplay(summary)
}

function compactionTokens(summary: ContextSummary) {
  return buildCompactionTokenDisplay(summary)
}

function compactionBeforeTokenText(summary: ContextSummary): string {
  const before = compactionTokens(summary).before
  return before == null ? '—' : `${formatTokenCount(before)} tokens`
}

function compactionTitle(summary: ContextSummary): string {
  return summary.status === 'Succeeded' ? '上下文已压缩' : '上下文压缩'
}

function compactionStatusClass(summary: ContextSummary): string {
  return `is-${summary.status.toLowerCase()}`
}

/** 构造消息内 markdown 的图片同步查找：键与 useFileHandling 的解析缓存一致。 */
function imageLookup(
  messageId: string,
  selfObjectKey?: string,
): ((src: string) => string | undefined) | undefined {
  if (!props.markdownImageUrls?.size) return undefined
  return (src: string): string | undefined => {
    if (!src || isSelfContainedImageRef(src)) return undefined
    return props.markdownImageUrls?.get(`${messageId}|${selfObjectKey ?? ''}|${src}`)
  }
}

async function copyTraceId(traceId: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(traceId)
    ElMessage.success('TraceId 已复制')
  } catch {
    ElMessage.warning('无法复制，请手动选择 TraceId')
  }
}

function distanceFromBottom(): number | null {
  const wrap = messagesScrollbar.value?.wrapRef
  if (!wrap) return null
  return wrap.scrollHeight - wrap.scrollTop - wrap.clientHeight
}

let lastObservedTop: number | null = null
let lastUpwardAt = 0
// 程序化滚动后的短暂窗口内忽略方向判定，避免与浏览器滚动事件竞态产生误判。
const PROGRAMMATIC_SCROLL_WINDOW_MS = 80
let programmaticUntil = 0

function handleScroll(): void {
  const wrap = messagesScrollbar.value?.wrapRef
  if (!wrap) return
  const now = Date.now()
  const suppressed = now < programmaticUntil
  let movedUp = false
  if (!suppressed && lastObservedTop != null && wrap.scrollTop < lastObservedTop - 1) {
    movedUp = true
    stickToBottom.value = false
    lastUpwardAt = now
  }
  lastObservedTop = wrap.scrollTop
  if (suppressed) return
  const distance = wrap.scrollHeight - wrap.scrollTop - wrap.clientHeight
  // 冷却期内贴底判定不得夺权，否则小幅上滑会被高频输出立即抵消。
  const cooling = now - lastUpwardAt < REENGAGE_COOLDOWN_MS
  if (!movedUp && !cooling && distance <= BOTTOM_STICK_THRESHOLD) stickToBottom.value = true
}

// 流式输出会高频把视口钉回底部，仅靠“距底阈值”永远攒不够上滑位移；
// 直接以向上滚动的意图（滚轮 deltaY<0）立即解除跟随。
function handleWheel(event: WheelEvent): void {
  if (event.deltaY < 0) stickToBottom.value = false
}

function scrollToBottom(force = false): void {
  if (!force && !stickToBottom.value) return
  const attempt = () => {
    if (!force && !stickToBottom.value) return
    programmaticUntil = Date.now() + PROGRAMMATIC_SCROLL_WINDOW_MS
    messagesScrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER)
  }
  attempt()
  requestAnimationFrame(attempt)
  stickToBottom.value = true
}
// 默认始终自动跟随；数组引用被替换（打开/切换会话）时强制归底一次，
// 深层变更（流式追加内容）仅在用户未上滑时跟随。immediate 保证首屏即到底部。
watch(
  () => props.messages,
  (value, previous) => {
    const replaced = value !== previous
    void nextTick(() => scrollToBottom(replaced || previous === undefined))
  },
  { deep: true, immediate: true },
)
watch(() => props.contextSummaries, () => { void nextTick(() => scrollToBottom()) }, { deep: true })
watch(
  () => props.streaming,
  (streaming, previous) => {
    if (streaming) {
      scrollToBottom()
      return
    }
    // 回复完成：Token 用量 / 模型名等页脚随后才插入布局，
    // 等两帧渲染稳定后再补一次归底；用户若已在阅读历史则不打扰。
    if (previous) {
      void nextTick(() => {
        scrollToBottom()
        requestAnimationFrame(() => requestAnimationFrame(() => scrollToBottom()))
      })
    }
  },
)

defineExpose({ scrollToBottom })
</script>

<template>
  <el-scrollbar ref="messagesScrollbar" class="messages" wrap-class="messages-wrap" v-loading="props.loading" @scroll="handleScroll" @wheel="handleWheel">
    <div v-if="!props.messages.length" class="welcome">
      <div class="welcome-icon">O</div>
      <h1>今天想处理什么？</h1>
      <p>由 Router 自动选择最合适的 Agent，或在顶部手动指定。</p>
      <div class="prompt-grid">
        <button v-for="item in suggestions" :key="item[0]" type="button" @click="emit('suggest', item[1])">
          <strong>{{ item[0] }}</strong><span>{{ item[1] }}</span>
        </button>
      </div>
    </div>

    <template v-for="item in timelineRows" :key="item.messageId">
    <div
      v-if="item.timelineKind === 'message'"
      class="message-row"
      :class="[item.role, { 'process-only': !hasMessageContent(item) && processActivities(item).length > 0 }]"
    >
      <div v-if="item.role === 'assistant'" class="assistant-mark" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none"><circle cx="6" cy="6" r="2.2" /><circle cx="18" cy="8" r="2.2" /><circle cx="11" cy="18" r="2.2" /><path d="M7.9 7.2 16 8M7 7.9l3.2 8.1M16.7 9.8l-4.3 6.5" /></svg>
      </div>

      <div class="message-content-column">
        <div v-if="item.role === 'assistant'" class="message-author">
          <strong>OpenAgent</strong>
          <span v-if="formatTimestamp(item.timestamp)">{{ formatTimestamp(item.timestamp) }}</span>
        </div>

        <details
          v-if="processActivities(item).length"
          class="process-activity process-bundle"
          :class="{ running: isThinking(item) }"
        >
          <summary>
            <span class="activity-icon thinking-icon"><i /><i /><i /></span>
            <span class="process-title">{{ isThinking(item) ? '正在执行' : '执行过程' }}</span>
            <small>{{ processSummary(item) }} · {{ isThinking(item) ? '进行中' : '已折叠' }}</small>
          </summary>
          <div class="process-bundle-body">
            <details
              v-for="(activity, activityIndex) in processActivities(item)"
              :key="activity.kind === 'tool' ? activity.tool.callId || `${activity.tool.name}-${activityIndex}` : `reasoning-${activityIndex}`"
              class="process-step"
              :class="{ running: activity.kind === 'tool' && !activity.tool.result && isStreamingItem(item) }"
            >
              <summary class="process-step-head">
                <span class="process-step-index">{{ activityIndex + 1 }}</span>
                <span class="process-step-copy">
                  <strong>{{ activity.kind === 'reasoning' ? '思考' : toolPresentation(activity.tool.name).displayName }}</strong>
                  <small>{{ activity.kind === 'reasoning' ? '模型推理' : toolPresentation(activity.tool.name).kind }}</small>
                </span>
                <span v-if="activity.kind === 'tool'" class="process-step-status" :class="{ done: activity.tool.result != null, running: isStreamingItem(item) && activity.tool.result == null }">
                  {{ toolStatusText(activity.tool, isStreamingItem(item)) }}
                </span>
                <span class="process-step-chevron">›</span>
              </summary>
              <div class="process-step-body">
                <pre v-if="activity.kind === 'reasoning'" class="process-reasoning">{{ activity.content }}</pre>
                <div v-else class="process-tool-body">
                  <div v-if="toolArgumentsText(activity.tool)" class="tool-section"><span>输入</span><pre class="tool-args">{{ toolArgumentsText(activity.tool) }}</pre></div>
                  <div v-if="toolResultText(activity.tool)" class="tool-section"><span>输出</span><pre class="tool-result">{{ toolResultText(activity.tool) }}</pre></div>
                  <div v-else class="tool-waiting"><span class="status-spinner" />{{ isStreamingItem(item) ? '等待工具返回结果…' : '本次调用未返回可展示结果' }}</div>
                </div>
              </div>
            </details>
          </div>
        </details>

        <div v-if="item.role === 'user' && item.files?.length" class="message-files user-files">
          <button v-for="file in item.files" :key="file.fileId || file.fileName" type="button" class="message-file upload-file" @click="emit('download', file)">
            <img v-if="file.previewUrl" :src="file.previewUrl" :alt="file.fileName" />
            <span v-else class="message-file-type">{{ fileLabel(file) }}</span>
            <span class="message-file-meta"><strong>{{ file.fileName }}</strong><small>已上传 · {{ formatFileSize(file.length) }}</small></span>
          </button>
        </div>

        <div v-if="item.content || isStreamingItem(item)" class="message-bubble"><MarkdownContent :content="item.content" :streaming="isStreamingItem(item) && Boolean(item.content)" :resolve-image="imageLookup(item.messageId)" /></div>

        <div v-if="shouldShowUsage(item)" class="message-usage" aria-label="当前响应 Token 用量">
          <span v-if="item.modelId" class="message-model">{{ item.modelId }}</span>
          <span :class="{ unavailable: !item.tokenUsage }">Token · {{ formatTokenUsage(item.tokenUsage) }}</span>
          <small v-if="formatTokenBreakdown(item.tokenUsage)">{{ formatTokenBreakdown(item.tokenUsage) }}</small>
        </div>

        <div v-if="item.role === 'assistant' && item.files?.length" class="generated-files">
          <div class="generated-files-heading"><span>生成的文件</span><small>{{ item.files.length }} 个可下载文件</small></div>
          <div class="message-files assistant-files">
            <!-- div 而非 button：卡片内嵌可交互的 markdown 预览折叠面板，button 不允许交互后代。 -->
            <div v-for="file in item.files" :key="file.fileId || file.fileName" class="message-file output-file" role="button" tabindex="0" @click="emit('download', file)" @keydown.enter.prevent="emit('download', file)">
              <img v-if="file.previewUrl" :src="file.previewUrl" :alt="file.fileName" />
              <span v-else class="message-file-type">{{ fileLabel(file) }}</span>
              <span class="message-file-meta"><strong>{{ file.fileName }}</strong><small>{{ formatFileSize(file.length) }} · 点击下载</small></span>
              <span class="message-file-action">↓</span>
              <details v-if="isMarkdownFile(file.mediaType, file.fileName) && file.previewText" class="message-file-markdown" open @click.stop @keydown.stop>
                <summary @click.stop>预览 Markdown</summary>
                <MarkdownContent :content="file.previewText" :resolve-image="imageLookup(item.messageId, file.objectKey)" />
              </details>
              <pre v-else-if="file.previewText" class="message-file-preview">{{ file.previewText }}</pre>
            </div>
          </div>
        </div>

        <div v-if="!hasMessageContent(item) && !processActivities(item).length && !item.error" class="message-thinking-placeholder" aria-live="polite">
          <span v-if="isStreamingItem(item)" class="thinking-dots"><i /><i /><i /></span><span>{{ isStreamingItem(item) ? '正在生成回复' : incompleteResponseText(item) }}</span>
        </div>

        <div v-if="item.error" class="message-error" role="alert">
          <span class="message-error-icon">
            <svg viewBox="0 0 20 20" fill="none"><path d="M10 6v4.5M10 14h.01" /><circle cx="10" cy="10" r="7.5" /></svg>
          </span>
          <div class="message-error-body">
            <strong>{{ item.error.title || 'Agent 执行失败' }}</strong>
            <p>{{ item.error.detail }}</p>
            <button v-if="item.error.traceId" type="button" class="message-error-trace" title="复制 TraceId" @click="copyTraceId(item.error.traceId)">TraceId · {{ item.error.traceId }}</button>
          </div>
        </div>
      </div>

      <div v-if="item.role === 'user'" class="user-mark" aria-hidden="true">{{ (props.currentUser?.userId || 'U').slice(0, 1).toUpperCase() }}</div>
    </div>

    <div
      v-else
      class="message-row context-summary-row"
    >
      <details class="context-summary-message">
        <summary class="context-summary-line">
          <span class="context-summary-chevron" aria-hidden="true">›</span>
          <span class="context-summary-icon" aria-hidden="true">↻</span>
          <strong>{{ compactionTitle(item.contextSummary) }}</strong>
          <span class="context-summary-brief">{{ compactionDisplay(item.contextSummary).trigger }} · {{ compactionDisplay(item.contextSummary).strategy }}</span>
          <time>{{ formatTimestamp(item.contextSummary.lastCompressedAt) }}</time>
          <span class="context-summary-status" :class="compactionStatusClass(item.contextSummary)">{{ compactionDisplay(item.contextSummary).status }}</span>
        </summary>
        <div class="context-summary-expanded">
          <div class="context-summary-meta">
            <span>范围 <strong>{{ item.contextSummary.originalStartSequence || '—' }}–{{ item.contextSummary.originalEndSequence || '—' }}</strong></span>
            <span>压缩前 <strong>{{ compactionBeforeTokenText(item.contextSummary) }}</strong></span>
            <span>压缩后 <strong>{{ formatTokenCount(compactionTokens(item.contextSummary).after) }} tokens</strong></span>
            <span>保留 <strong>{{ compactionTokens(item.contextSummary).retainedPercent }}</strong></span>
          </div>
          <div class="context-summary-body"><MarkdownContent :content="compactionDisplay(item.contextSummary).detail" /></div>
          <small v-if="compactionDisplay(item.contextSummary).recovered" class="context-summary-recovered">原始历史已恢复，本次模型上下文未丢失。</small>
        </div>
      </details>
    </div>
    </template>
  </el-scrollbar>
</template>
