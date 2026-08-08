<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { ref } from 'vue'
import type { PendingAttachment } from '../types'

const props = defineProps<{
  modelValue: string
  gatewayUrl: string
  selectedAgentId: string
  loading: boolean
  pendingAttachments: PendingAttachment[]
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  send: []
  'attachments-change': [attachments: PendingAttachment[]]
}>()

const attachmentInput = ref<HTMLInputElement | null>(null)

const maxAttachmentCount = 5
const maxAttachmentSize = 10 * 1024 * 1024
const maxAttachmentTotalSize = 25 * 1024 * 1024

function formatFileSize(size: number): string {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${Math.ceil(size / 1024)} KB`
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}

function handleAttachmentChange(event: Event): void {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files || [])
  input.value = ''
  if (!files.length) return

  const currentSize = props.pendingAttachments.reduce((total, item) => total + item.file.size, 0)
  const availableCount = maxAttachmentCount - props.pendingAttachments.length
  if (availableCount <= 0) {
    ElMessage.error(`最多上传 ${maxAttachmentCount} 个文件`)
    return
  }

  const accepted: PendingAttachment[] = []
  let totalSize = currentSize
  for (const file of files.slice(0, availableCount)) {
    if (file.size > maxAttachmentSize) {
      ElMessage.error(`${file.name} 超过单文件 ${formatFileSize(maxAttachmentSize)} 限制`)
      continue
    }
    if (totalSize + file.size > maxAttachmentTotalSize) {
      ElMessage.error(`附件总大小不能超过 ${formatFileSize(maxAttachmentTotalSize)}`)
      break
    }
    totalSize += file.size
    accepted.push({ id: crypto.randomUUID(), file })
  }
  emit('attachments-change', [...props.pendingAttachments, ...accepted])
}

function removeAttachment(id: string): void {
  emit('attachments-change', props.pendingAttachments.filter(item => item.id !== id))
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  emit('send')
}

function openAttachmentPicker(): void {
  attachmentInput.value?.click()
}
</script>

<template>
  <div class="composer">
    <div v-if="props.pendingAttachments.length" class="attachment-list">
      <div v-for="item in props.pendingAttachments" :key="item.id" class="attachment-chip">
        <span class="attachment-icon">↗</span><span class="attachment-name" :title="item.file.name">{{ item.file.name }}</span><span class="attachment-size">{{ formatFileSize(item.file.size) }}</span>
        <el-button link class="attachment-remove" @click="removeAttachment(item.id)">×</el-button>
      </div>
    </div>
    <el-input :model-value="props.modelValue" type="textarea" :rows="2" resize="none" placeholder="向 Agent 发送消息" @update:model-value="emit('update:modelValue', $event)" @keydown="handleKeydown" />
    <input ref="attachmentInput" class="attachment-input" type="file" multiple accept=".png,.jpg,.jpeg,.gif,.webp,.pdf,.json,.txt,.csv,.md" @change="handleAttachmentChange" />
    <div class="composer-footer"><div class="composer-hints"><el-button text class="attach-button" @click="openAttachmentPicker">＋ 文件</el-button><span>最多 5 个 · 25 MB</span></div><div class="composer-actions"><span class="gateway-caption">Gateway · {{ props.gatewayUrl || '未配置' }}</span><span class="keyboard-hint">↵ 发送</span><el-button type="primary" circle aria-label="发送" :loading="props.loading" :disabled="!props.selectedAgentId || (!props.modelValue.trim() && !props.pendingAttachments.length)" @click="emit('send')">↑</el-button></div></div>
  </div>
</template>
