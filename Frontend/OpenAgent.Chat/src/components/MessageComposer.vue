<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { ref } from 'vue'
import { formatFileSize } from '../messagePresentation'
import type { PendingFile } from '../types'

const props = defineProps<{
  modelValue: string
  endpointUrl: string
  endpointLabel: string
  selectedAgentId: string
  loading: boolean
  pendingFiles: PendingFile[]
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  send: []
  'files-change': [files: PendingFile[]]
  'retry-file': [id: string]
}>()

const fileInput = ref<HTMLInputElement | null>(null)

const maxFileCount = 5
const maxFileSize = 10 * 1024 * 1024
const maxFileTotalSize = 25 * 1024 * 1024

function handleFileChange(event: Event): void {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files || [])
  input.value = ''
  if (!files.length) return

  const currentSize = props.pendingFiles.reduce((total, item) => total + item.file.size, 0)
  const availableCount = maxFileCount - props.pendingFiles.length
  if (availableCount <= 0) {
    ElMessage.error(`最多上传 ${maxFileCount} 个文件`)
    return
  }

  const accepted: PendingFile[] = []
  let totalSize = currentSize
  for (const file of files.slice(0, availableCount)) {
    if (file.size > maxFileSize) {
      ElMessage.error(`${file.name} 超过单文件 ${formatFileSize(maxFileSize)} 限制`)
      continue
    }
    if (totalSize + file.size > maxFileTotalSize) {
      ElMessage.error(`文件总大小不能超过 ${formatFileSize(maxFileTotalSize)}`)
      break
    }
    totalSize += file.size
    accepted.push({ id: crypto.randomUUID(), file, state: 'uploading' })
  }
  emit('files-change', [...props.pendingFiles, ...accepted])
}

function removeFile(id: string): void {
  emit('files-change', props.pendingFiles.filter(item => item.id !== id))
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  emit('send')
}

function openFilePicker(): void {
  fileInput.value?.click()
}
</script>

<template>
  <div class="composer">
    <div v-if="props.pendingFiles.length" class="file-list">
      <div class="file-list-heading"><span>附件</span><small>{{ props.pendingFiles.length }} / {{ maxFileCount }}</small></div>
      <div v-for="item in props.pendingFiles" :key="item.id" class="file-chip">
        <span class="file-icon" :class="item.state"><svg v-if="item.state === 'ready'" viewBox="0 0 20 20" fill="none"><path d="m5 10 3 3 7-7" /></svg><svg v-else-if="item.state === 'failed'" viewBox="0 0 20 20" fill="none"><path d="M10 5v6m0 3h.01" /></svg><span v-else class="status-spinner" /></span>
        <span class="file-copy"><strong class="file-name" :title="item.file.name">{{ item.file.name }}</strong><small>{{ formatFileSize(item.file.size) }} · <span class="file-status" :class="item.state">{{ item.state === 'ready' ? '已上传' : item.state === 'failed' ? '上传失败' : '上传中' }}</span></small></span>
        <el-button v-if="item.state === 'failed'" link class="file-retry" @click="emit('retry-file', item.id)">重试</el-button>
        <el-button link class="file-remove" aria-label="移除附件" @click="removeFile(item.id)">×</el-button>
      </div>
    </div>
    <div class="composer-box">
      <el-input :model-value="props.modelValue" type="textarea" :rows="2" resize="none" placeholder="向 Agent 发送消息" @update:model-value="emit('update:modelValue', $event)" @keydown="handleKeydown" />
      <input ref="fileInput" class="file-input" type="file" multiple accept=".png,.jpg,.jpeg,.gif,.webp,.pdf,.json,.txt,.csv,.md" @change="handleFileChange" />
      <div class="composer-footer">
        <div class="composer-hints"><el-button text class="file-button" aria-label="添加附件" @click="openFilePicker"><svg viewBox="0 0 20 20" fill="none"><path d="M7 10.8 11.8 6a2.1 2.1 0 1 1 3 3l-6.2 6.2a3.5 3.5 0 0 1-5-5L10 3.8" /></svg><span>添加附件</span></el-button><span>最多 5 个 · 25 MB</span></div>
        <div class="composer-actions"><span class="connection-caption">{{ props.endpointLabel }} · {{ props.endpointUrl || '未配置' }}</span><span class="keyboard-hint">Enter 发送</span><el-button type="primary" circle aria-label="发送" :loading="props.loading" :disabled="!props.selectedAgentId || (!props.modelValue.trim() && !props.pendingFiles.length) || props.pendingFiles.some(item => item.state !== 'ready')" @click="emit('send')"><svg viewBox="0 0 20 20" fill="none"><path d="M10 15V5m0 0L6 9m4-4 4 4" /></svg></el-button></div>
      </div>
    </div>
  </div>
</template>
