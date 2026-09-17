<script setup lang="ts">
import { computed } from 'vue'
import {
  describeTurnMessages,
  formatDuration,
  groupInteractions,
  prettyJson,
  sourceLabel,
  statusLabel,
  type InteractionGroup,
} from '../interactionTimeline'
import type { ConversationRecord, LlmInteraction } from '../types'

const props = defineProps<{
  interactions: LlmInteraction[]
  conversation?: ConversationRecord | null
  loading?: boolean
  /** 重放会话使用内嵌日志，不提供在线加载。 */
  replay?: boolean
}>()

const emit = defineEmits<{ load: []; refresh: [] }>()

const groups = computed<InteractionGroup[]>(() => groupInteractions(props.interactions))
const callCount = computed(() => props.interactions.length)

function statusClass(item: LlmInteraction): string {
  const label = statusLabel(item)
  return label === 'Succeeded' ? 'ok' : label === 'Failed' ? 'fail' : 'cancel'
}

function shortTrace(traceId: string): string {
  return traceId.length > 10 ? `${traceId.slice(0, 10)}…` : traceId
}

function formatTime(iso: string): string {
  if (!iso) return '—'
  try {
    return new Date(iso).toLocaleTimeString()
  } catch {
    return iso
  }
}

function tokenSummary(item: LlmInteraction): string {
  const usage = item.tokenUsage
  if (!usage) return '—'
  return `${usage.totalTokens} tk`
}
</script>

<template>
  <div class="interaction-timeline">
    <div v-if="props.loading" class="interaction-empty">交互日志加载中…</div>
    <template v-else-if="groups.length">
      <div class="interaction-toolbar">
        <span class="interaction-summary">{{ callCount }} 次模型调用 · {{ groups.length }} 轮</span>
        <button v-if="!props.replay" class="interaction-refresh" type="button" @click="emit('refresh')">刷新</button>
      </div>
      <div v-for="group in groups" :key="group.traceId || group.startedAt" class="interaction-group" :data-status="group.status">
        <div class="interaction-group-head">
          <span class="interaction-turn-badge">{{ sourceLabel(group.items[0]) }}轮</span>
          <span class="interaction-turn-id" :title="group.traceId">{{ shortTrace(group.traceId) || '无 TraceId' }}</span>
          <span class="interaction-turn-meta">
            {{ formatTime(group.startedAt) }}
            <template v-if="describeTurnMessages(props.conversation, group.traceId)"> · {{ describeTurnMessages(props.conversation, group.traceId) }}</template>
            · {{ formatDuration(group.items.reduce((sum, item) => sum + item.durationMs, 0)) }}
            <template v-if="group.totalTokens"> · {{ group.totalTokens }} tk</template>
          </span>
        </div>
        <details v-for="item in group.items" :key="item.interactionId" class="interaction-item">
          <summary>
            <span class="interaction-badge" :class="statusClass(item)">{{ statusLabel(item) === 'Succeeded' ? '成功' : statusLabel(item) === 'Failed' ? '失败' : '取消' }}</span>
            <span class="interaction-model" :title="`${item.provider || ''} ${item.apiFormat || ''}`">{{ item.modelId }}</span>
            <span class="interaction-item-meta">#{{ item.callIndex }} {{ item.streamed ? '流式' : '同步' }} · {{ formatDuration(item.durationMs) }} · {{ tokenSummary(item) }} · {{ sourceLabel(item) }}</span>
          </summary>
          <div v-if="item.errorMessage" class="interaction-error">{{ item.errorMessage }}</div>
          <div class="interaction-payloads">
            <details class="interaction-payload" open>
              <summary>请求载荷</summary>
              <pre>{{ prettyJson(item.requestJson) || '（无）' }}</pre>
            </details>
            <details class="interaction-payload">
              <summary>响应载荷</summary>
              <pre>{{ prettyJson(item.responseJson) || '（无）' }}</pre>
            </details>
          </div>
        </details>
      </div>
    </template>
    <div v-else class="interaction-empty">
      <template v-if="props.replay">该日志未内嵌交互日志（旧版导出或服务端不可用）</template>
      <template v-else>
        暂无交互日志
        <button class="interaction-refresh" type="button" @click="emit('load')">加载</button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.interaction-timeline {
  display: flex;
  flex-direction: column;
  gap: 8px;
  font-size: 12px;
  max-height: 420px;
  overflow: auto;
}

.interaction-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.interaction-summary {
  color: var(--el-text-color-secondary);
}

.interaction-refresh {
  border: none;
  background: none;
  color: var(--el-color-primary);
  cursor: pointer;
  padding: 0;
  font-size: 12px;
}

.interaction-refresh:hover {
  text-decoration: underline;
}

.interaction-group {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 6px 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.interaction-group[data-status='Failed'] {
  border-color: var(--el-color-danger-light-5);
}

.interaction-group-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.interaction-turn-badge {
  background: var(--el-fill-color);
  border-radius: 4px;
  padding: 1px 6px;
  font-weight: 600;
}

.interaction-turn-id {
  font-family: ui-monospace, monospace;
  color: var(--el-text-color-secondary);
}

.interaction-turn-meta {
  color: var(--el-text-color-secondary);
  word-break: break-all;
}

.interaction-item summary {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  cursor: pointer;
  list-style: none;
  padding: 2px 0;
}

.interaction-item summary::-webkit-details-marker {
  display: none;
}

.interaction-badge {
  border-radius: 4px;
  padding: 0 5px;
  font-size: 11px;
}

.interaction-badge.ok {
  background: var(--el-color-success-light-9);
  color: var(--el-color-success);
}

.interaction-badge.fail {
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
}

.interaction-badge.cancel {
  background: var(--el-color-warning-light-9);
  color: var(--el-color-warning);
}

.interaction-model {
  font-weight: 600;
  word-break: break-all;
}

.interaction-item-meta {
  color: var(--el-text-color-secondary);
  word-break: break-all;
}

.interaction-error {
  color: var(--el-color-danger);
  padding: 2px 0;
  word-break: break-all;
}

.interaction-payloads {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.interaction-payload summary {
  cursor: pointer;
  color: var(--el-text-color-secondary);
}

.interaction-payload pre {
  margin: 4px 0 0;
  padding: 6px;
  background: var(--el-fill-color-light);
  border-radius: 4px;
  font-size: 11px;
  line-height: 1.5;
  max-height: 260px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
}

.interaction-empty {
  color: var(--el-text-color-secondary);
  display: flex;
  gap: 8px;
  align-items: center;
}
</style>
