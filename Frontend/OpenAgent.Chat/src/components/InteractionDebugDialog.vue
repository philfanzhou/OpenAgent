<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api'
import {
  collectAllInteractions,
  formatInteractionTime,
  interactionSourceLabel,
  interactionStatusLabel,
  interactionStatusTagType,
  prettyInteractionPayload,
  shortTraceId,
} from '../interactionPresentation'
import type { LlmInteractionRecord } from '../types'

const props = defineProps<{
  open: boolean
  conversationId: string | null
  conversationTitle?: string
}>()

const emit = defineEmits<{ 'update:open': [value: boolean] }>()

const loading = ref(false)
const records = ref<LlmInteractionRecord[]>([])
const loadError = ref('')

async function load(): Promise<void> {
  if (!props.conversationId) return
  loading.value = true
  loadError.value = ''
  try {
    records.value = await collectAllInteractions(
      (skip, take) => api.listLlmInteractions(props.conversationId!, skip, take),
    )
  } catch (error) {
    records.value = []
    loadError.value = error instanceof Error ? error.message : '请求失败'
  } finally {
    loading.value = false
  }
}

function refresh(): void {
  void load()
}

watch(
  () => [props.open, props.conversationId] as const,
  ([open]) => {
    if (open) void load()
  },
)

function notifyFailure(): void {
  if (loadError.value) ElMessage.error(loadError.value)
}
</script>

<template>
  <el-dialog
    :model-value="open"
    class="interaction-debug-dialog"
    width="min(1080px, calc(100vw - 40px))"
    top="4vh"
    destroy-on-close
    append-to-body
    @update:model-value="emit('update:open', $event)"
    @open="notifyFailure"
  >
    <template #header>
      <div class="interaction-debug-head">
        <div>
          <span class="context-label">INTERACTIONS</span>
          <strong>会话交互记录</strong>
          <small class="table-subtext" :title="conversationId ?? undefined">{{ conversationTitle || conversationId }}</small>
        </div>
        <div class="interaction-debug-actions">
          <small class="table-subtext">{{ records.length }} 条记录</small>
          <el-button size="small" :loading="loading" @click="refresh">刷新</el-button>
        </div>
      </div>
    </template>

    <el-table
      v-loading="loading"
      :data="records"
      class="capability-table interaction-debug-table"
      empty-text="暂无交互记录"
      size="small"
    >
      <el-table-column type="expand">
        <template #default="scope">
          <div class="interaction-detail">
            <p v-if="scope.row.errorMessage" class="interaction-detail-error">错误：{{ scope.row.errorMessage }}</p>
            <div class="interaction-detail-block">
              <span class="context-label">REQUEST</span>
              <pre>{{ prettyInteractionPayload(scope.row.requestJson) || '（未记录）' }}</pre>
            </div>
            <div class="interaction-detail-block">
              <span class="context-label">RESPONSE</span>
              <pre>{{ prettyInteractionPayload(scope.row.responseJson) || '（未记录）' }}</pre>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="#" width="52">
        <template #default="scope">{{ scope.row.callIndex }}</template>
      </el-table-column>
      <el-table-column label="时间" width="96">
        <template #default="scope">{{ formatInteractionTime(scope.row.startedAt) }}</template>
      </el-table-column>
      <el-table-column label="轮次 TraceId" width="130">
        <template #default="scope"><code :title="scope.row.traceId">{{ shortTraceId(scope.row.traceId) }}</code></template>
      </el-table-column>
      <el-table-column label="来源" width="96">
        <template #default="scope">{{ interactionSourceLabel(scope.row.source) }}</template>
      </el-table-column>
      <el-table-column label="模型" min-width="130" show-overflow-tooltip>
        <template #default="scope">{{ scope.row.modelId }}</template>
      </el-table-column>
      <el-table-column label="状态" width="90">
        <template #default="scope"><el-tag size="small" round :type="interactionStatusTagType(scope.row.status)">{{ interactionStatusLabel(scope.row.status) }}</el-tag></template>
      </el-table-column>
      <el-table-column label="耗时" width="90">
        <template #default="scope">{{ scope.row.durationMs }} ms</template>
      </el-table-column>
      <el-table-column label="Tokens (入/出/总)" width="150">
        <template #default="scope">
          {{ scope.row.tokenUsage ? `${scope.row.tokenUsage.promptTokens} / ${scope.row.tokenUsage.completionTokens} / ${scope.row.tokenUsage.totalTokens}` : '—' }}
        </template>
      </el-table-column>
    </el-table>

    <p v-if="loadError && !loading" class="interaction-debug-error">{{ loadError }}</p>
  </el-dialog>
</template>

<style scoped>
.interaction-debug-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.interaction-debug-head strong {
  margin-right: 8px;
  font-size: 15px;
}

.interaction-debug-actions {
  display: flex;
  align-items: center;
  gap: 10px;
}

.interaction-debug-table code {
  font-family: var(--font-mono);
  font-size: 12px;
}

.interaction-detail {
  display: grid;
  gap: 10px;
  padding: 4px 8px;
}

.interaction-detail-error {
  margin: 0;
  color: var(--danger);
  font-size: 13px;
}

.interaction-detail-block {
  display: grid;
  gap: 4px;
}

.interaction-detail-block pre {
  margin: 0;
  padding: 10px;
  max-height: 320px;
  overflow: auto;
  background: var(--bg-subtle);
  border: 1px solid var(--border);
  border-radius: var(--r-sm);
  font-family: var(--font-mono);
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.interaction-debug-error {
  margin: 10px 0 0;
  color: var(--danger);
  font-size: 13px;
}
</style>
