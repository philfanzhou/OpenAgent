<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import type { ConversationMessage, MessageAttachment } from '../types'

const props = defineProps<{
  messages: ConversationMessage[]
  loading: boolean
}>()

const emit = defineEmits<{
  suggest: [value: string]
  download: [attachment: MessageAttachment]
}>()

const suggestions = [
  ['分析一个需求', '帮我拆解这个需求，并给出可执行计划。'],
  ['检查服务状态', '检查当前服务状态和可用 Agent。'],
  ['总结技术方案', '请用简洁的结构总结当前技术方案。'],
]

const messagesScrollbar = ref<{ setScrollTop: (value: number) => void } | null>(null)

watch(() => props.messages, () => {
  void nextTick(() => messagesScrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER))
}, { deep: true })
</script>

<template>
  <el-scrollbar ref="messagesScrollbar" class="messages" wrap-class="messages-wrap" v-loading="props.loading">
    <div v-if="!props.messages.length" class="welcome"><div class="welcome-icon">O</div><h1>今天想处理什么？</h1><p>由 Router 自动选择最合适的 Agent，或在顶部手动指定。</p><div class="prompt-grid"><button v-for="item in suggestions" :key="item[0]" type="button" @click="emit('suggest', item[1])"><strong>{{ item[0] }}</strong><span>{{ item[1] }}</span></button></div></div>
    <div v-for="item in props.messages" :key="item.messageId" class="message-row" :class="item.role">
      <div class="avatar">{{ item.role === 'user' ? '我' : item.role === 'tool' ? '工具' : 'AI' }}</div>
      <div class="message-bubble"><div v-if="item.toolName" class="tool-tag">调用工具：{{ item.toolName }}</div><div v-if="item.attachments?.length" class="message-attachments"><button v-for="attachment in item.attachments" :key="attachment.fileId || attachment.fileName" type="button" class="message-attachment" @click="emit('download', attachment)"><img v-if="attachment.previewUrl" :src="attachment.previewUrl" :alt="attachment.fileName" /><span>↗ {{ attachment.fileName }}</span><pre v-if="attachment.previewText">{{ attachment.previewText }}</pre></button></div><div class="message-content">{{ item.content || '…' }}</div></div>
    </div>
  </el-scrollbar>
</template>
