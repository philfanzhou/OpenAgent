<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import type { ConversationMessage } from '../types'

const props = defineProps<{
  messages: ConversationMessage[]
  loading: boolean
}>()

const messagesScrollbar = ref<{ setScrollTop: (value: number) => void } | null>(null)

watch(() => props.messages, () => {
  void nextTick(() => messagesScrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER))
}, { deep: true })
</script>

<template>
  <el-scrollbar ref="messagesScrollbar" class="messages" wrap-class="messages-wrap" v-loading="props.loading">
    <div v-if="!props.messages.length" class="welcome"><div class="welcome-orbit"><div class="welcome-icon">✦</div><span class="orbit-dot orbit-dot-one" /><span class="orbit-dot orbit-dot-two" /></div><h1>你好，今天想完成什么？</h1><p>把问题、文件或灵感交给你的 Agent，一起把事情做好。</p></div>
    <div v-for="item in props.messages" :key="item.messageId" class="message-row" :class="item.role">
      <div class="avatar">{{ item.role === 'user' ? '我' : item.role === 'tool' ? '工具' : 'AI' }}</div>
      <div class="message-bubble"><div v-if="item.toolName" class="tool-tag">调用工具：{{ item.toolName }}</div><div v-if="item.attachments?.length" class="message-attachments"><div v-for="attachment in item.attachments" :key="attachment.fileName" class="message-attachment"><img v-if="attachment.previewUrl" :src="attachment.previewUrl" :alt="attachment.fileName" /><span>↗ {{ attachment.fileName }}</span></div></div><div class="message-content">{{ item.content || '…' }}</div></div>
    </div>
  </el-scrollbar>
</template>
