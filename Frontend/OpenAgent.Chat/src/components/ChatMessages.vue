<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { ConversationMessage, MessageFile, ToolActivity } from '../types'

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

const displayMessages = computed(() => {
  const result: ConversationMessage[] = []
  let pendingReasoning = ''
  let pendingTools: ToolActivity[] = []

  for (const message of props.messages) {
    const isStoredToolCall = message.role === 'assistant'
      && !message.content
      && !message.files?.length
      && Boolean(message.toolName)
    if (isStoredToolCall) {
      pendingReasoning += message.reasoning || ''
      pendingTools.push({ name: message.toolName || '工具', callId: message.toolCallId })
      continue
    }

    if (message.role === 'tool') {
      let index = pendingTools.length - 1
      if (message.toolCallId) {
        for (let current = pendingTools.length - 1; current >= 0; current -= 1) {
          if (pendingTools[current]?.callId === message.toolCallId) {
            index = current
            break
          }
        }
      }
      if (index >= 0) pendingTools[index] = { ...pendingTools[index], result: message.content }
      else pendingTools.push({ name: message.toolName || '工具', callId: message.toolCallId, result: message.content })
      continue
    }

    if (message.role === 'assistant') {
      result.push({
        ...message,
        reasoning: `${pendingReasoning}${message.reasoning || ''}` || undefined,
        toolActivities: [...pendingTools, ...(message.toolActivities || [])],
      })
      pendingReasoning = ''
      pendingTools = []
      continue
    }

    result.push(message)
  }

  if (pendingReasoning || pendingTools.length) {
    result.push({
      messageId: 'pending-agent-process',
      sequence: Number.MAX_SAFE_INTEGER,
      role: 'assistant',
      content: '',
      timestamp: new Date().toISOString(),
      reasoning: pendingReasoning || undefined,
      toolActivities: pendingTools,
    })
  }
  return result
})

function processSummary(message: ConversationMessage): string {
  const toolCount = message.toolActivities?.length || 0
  if (message.reasoning && toolCount) return `思考并调用 ${toolCount} 个工具`
  if (toolCount) return `调用 ${toolCount} 个工具`
  return '查看思考过程'
}

function hasMessageContent(message: ConversationMessage): boolean {
  return Boolean(message.content || message.files?.length)
}

watch(() => props.messages, () => {
  void nextTick(() => messagesScrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER))
}, { deep: true })
</script>

<template>
  <el-scrollbar ref="messagesScrollbar" class="messages" wrap-class="messages-wrap" v-loading="props.loading">
    <div v-if="!props.messages.length" class="welcome"><div class="welcome-icon">O</div><h1>今天想处理什么？</h1><p>由 Router 自动选择最合适的 Agent，或在顶部手动指定。</p><div class="prompt-grid"><button v-for="item in suggestions" :key="item[0]" type="button" @click="emit('suggest', item[1])"><strong>{{ item[0] }}</strong><span>{{ item[1] }}</span></button></div></div>
    <div v-for="item in displayMessages" :key="item.messageId" class="message-row" :class="[item.role, { 'process-only': !hasMessageContent(item) && Boolean(item.reasoning || item.toolActivities?.length) }]">
      <div class="avatar">{{ item.role === 'user' ? '我' : 'AI' }}</div>
      <div class="message-content-column">
        <details v-if="item.reasoning || item.toolActivities?.length" class="process-activity">
          <summary><span class="process-title">{{ item.reasoning ? '思考与过程' : '工具调用' }}</span><small>{{ processSummary(item) }}</small></summary>
          <div class="process-activity-body">
            <div v-if="item.reasoning" class="reasoning-block"><span class="process-kind">思考</span><pre class="reasoning-content">{{ item.reasoning }}</pre></div>
            <ul v-if="item.toolActivities?.length" class="tool-activity-list"><li v-for="tool in item.toolActivities" :key="tool.callId || tool.name"><div class="tool-activity-heading"><span class="tool-status">{{ tool.result == null ? '正在调用' : '已完成' }}</span><strong>{{ tool.name }}</strong></div><pre v-if="tool.result" class="tool-result">{{ tool.result }}</pre></li></ul>
          </div>
        </details>
        <div v-if="hasMessageContent(item)" class="message-bubble"><div v-if="item.files?.length" class="message-files"><button v-for="file in item.files" :key="file.fileId || file.fileName" type="button" class="message-file" @click="emit('download', file)"><img v-if="file.previewUrl" :src="file.previewUrl" :alt="file.fileName" /><span>↗ {{ file.fileName }}</span><pre v-if="file.previewText">{{ file.previewText }}</pre></button></div><div v-if="item.content" class="message-content">{{ item.content }}</div></div>
        <div v-else-if="!item.reasoning && !item.toolActivities?.length" class="message-bubble"><div class="message-content">…</div></div>
      </div>
    </div>
  </el-scrollbar>
</template>
