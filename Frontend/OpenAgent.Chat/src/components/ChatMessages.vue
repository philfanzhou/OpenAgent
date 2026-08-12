<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { computed, nextTick, ref, watch } from 'vue'
import { buildDisplayMessages, fileLabel, formatFileSize, toolArgumentsText } from '../messagePresentation'
import type { ConversationMessage, MessageFile, ToolActivity } from '../types'
import MarkdownContent from './MarkdownContent.vue'

const props = defineProps<{
  messages: ConversationMessage[]
  loading: boolean
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

const messagesScrollbar = ref<{ setScrollTop: (value: number) => void } | null>(null)
const displayMessages = computed(() => buildDisplayMessages(props.messages))

function hasMessageContent(message: ConversationMessage): boolean {
  return Boolean(message.content || message.files?.length)
}

function isStreamingItem(message: ConversationMessage, index: number): boolean {
  return props.loading && message.role === 'assistant' && index === displayMessages.value.length - 1
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

async function copyTraceId(traceId: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(traceId)
    ElMessage.success('TraceId 已复制')
  } catch {
    ElMessage.warning('无法复制，请手动选择 TraceId')
  }
}

watch(() => props.messages, () => {
  void nextTick(() => messagesScrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER))
}, { deep: true })
</script>

<template>
  <el-scrollbar ref="messagesScrollbar" class="messages" wrap-class="messages-wrap" v-loading="props.loading">
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

    <div
      v-for="(item, index) in displayMessages"
      :key="item.messageId"
      class="message-row"
      :class="[item.role, { 'process-only': !hasMessageContent(item) && Boolean(item.reasoning || item.toolActivities?.length) }]"
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
          v-if="item.reasoning"
          class="process-activity reasoning-activity"
          :class="{ running: isStreamingItem(item, index) }"
          :open="isStreamingItem(item, index)"
        >
          <summary>
            <span class="activity-icon thinking-icon"><i /><i /><i /></span>
            <span class="process-title">{{ isStreamingItem(item, index) ? '正在思考' : '思考过程' }}</span>
            <small>{{ isStreamingItem(item, index) ? '生成中' : '已完成 · 展开查看' }}</small>
          </summary>
          <div class="process-activity-body"><MarkdownContent :content="item.reasoning" /></div>
        </details>

        <section v-if="item.toolActivities?.length" class="tool-activity-group" aria-label="工具调用">
          <div class="activity-group-label"><span>操作记录</span><small>{{ item.toolActivities.length }} 项</small></div>
          <details
            v-for="tool in item.toolActivities"
            :key="tool.callId || tool.name"
            class="tool-card"
            :class="{ running: tool.result == null && isStreamingItem(item, index) }"
          >
            <summary class="tool-card-head">
              <span class="activity-icon tool-card-icon">
                <svg viewBox="0 0 20 20" fill="none"><path d="M8.1 3.2a4.1 4.1 0 0 0 4.7 5.2l3.4 3.4a1.5 1.5 0 0 1 0 2.1l-2.3 2.3a1.5 1.5 0 0 1-2.1 0l-3.4-3.4a4.1 4.1 0 0 0-5.2-4.7l2.6 2.6 2.9-2.9-2.6-2.6Z" /></svg>
              </span>
              <span class="tool-card-copy"><strong class="tool-card-name">{{ tool.name }}</strong><small>Tool call</small></span>
              <span class="tool-status" :class="{ running: tool.result == null && isStreamingItem(item, index), done: tool.result != null }">{{ toolStatusText(tool, isStreamingItem(item, index)) }}</span>
              <span class="tool-chevron">›</span>
            </summary>
            <div class="tool-card-body">
              <div v-if="toolArgumentsText(tool)" class="tool-section"><span>输入</span><pre class="tool-args">{{ toolArgumentsText(tool) }}</pre></div>
              <div v-if="toolResultText(tool)" class="tool-section"><span>输出</span><pre class="tool-result">{{ toolResultText(tool) }}</pre></div>
              <div v-else class="tool-waiting"><span class="status-spinner" />{{ isStreamingItem(item, index) ? '等待工具返回结果…' : '本次调用未返回可展示结果' }}</div>
            </div>
          </details>
        </section>

        <div v-if="item.role === 'user' && item.files?.length" class="message-files user-files">
          <button v-for="file in item.files" :key="file.fileId || file.fileName" type="button" class="message-file upload-file" @click="emit('download', file)">
            <img v-if="file.previewUrl" :src="file.previewUrl" :alt="file.fileName" />
            <span v-else class="message-file-type">{{ fileLabel(file) }}</span>
            <span class="message-file-meta"><strong>{{ file.fileName }}</strong><small>已上传 · {{ formatFileSize(file.length) }}</small></span>
          </button>
        </div>

        <div v-if="item.content" class="message-bubble"><MarkdownContent :content="item.content" /></div>

        <div v-if="item.role === 'assistant' && item.files?.length" class="generated-files">
          <div class="generated-files-heading"><span>生成的文件</span><small>{{ item.files.length }} 个可下载文件</small></div>
          <div class="message-files assistant-files">
            <button v-for="file in item.files" :key="file.fileId || file.fileName" type="button" class="message-file output-file" @click="emit('download', file)">
              <img v-if="file.previewUrl" :src="file.previewUrl" :alt="file.fileName" />
              <span v-else class="message-file-type">{{ fileLabel(file) }}</span>
              <span class="message-file-meta"><strong>{{ file.fileName }}</strong><small>{{ formatFileSize(file.length) }} · 点击下载</small></span>
              <span class="message-file-action">↓</span>
              <pre v-if="file.previewText" class="message-file-preview">{{ file.previewText }}</pre>
            </button>
          </div>
        </div>

        <div v-if="!hasMessageContent(item) && !item.reasoning && !item.toolActivities?.length && !item.error" class="message-thinking-placeholder" aria-live="polite">
          <span class="thinking-dots"><i /><i /><i /></span><span>正在生成回复</span>
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
    </div>
  </el-scrollbar>
</template>
