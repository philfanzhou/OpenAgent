<script setup lang="ts">
import { computed } from 'vue'
import { AUTO_AGENT_ID, type AgentSummary, type LlmProviderProfile } from '../types'

const props = defineProps<{
  statusText: string
  agents: AgentSummary[]
  selectedAgentId: string
  llmProfiles: LlmProviderProfile[]
  selectedLlmProfileId: string
  allowAuto: boolean
  refreshingAgents: boolean
  title: string
  themeMode: 'light' | 'dark'
}>()

const emit = defineEmits<{
  'update:selectedAgentId': [value: string]
  'update:selectedLlmProfileId': [value: string]
  'agent-change': []
  'refresh-agents': []
  settings: []
  'toggle-theme': []
}>()

const selectedAgent = computed({
  get: () => props.selectedAgentId,
  set: value => emit('update:selectedAgentId', value),
})

const activeAgent = computed(() => props.agents.find(agent => agent.agentId === props.selectedAgentId))
const selectedLlm = computed({
  get: () => props.selectedLlmProfileId,
  set: value => emit('update:selectedLlmProfileId', value),
})
</script>

<template>
  <header class="topbar">
    <div class="topbar-copy"><span class="topbar-kicker">WORKSPACE / CHAT</span><strong>{{ props.title }}</strong></div>
    <div class="topbar-actions">
      <span class="connection-pill"><i :class="{ connected: props.statusText === '已连接' }" />{{ props.statusText }}</span>
      <el-select v-model="selectedAgent" class="agent-select" placeholder="选择 Agent" @change="emit('agent-change')">
        <el-option v-if="props.allowAuto" label="Auto · 意图路由" :value="AUTO_AGENT_ID" />
        <el-option v-for="agent in props.agents" :key="agent.agentId" :label="agent.name || agent.agentId" :value="agent.agentId" />
      </el-select>
      <el-select v-model="selectedLlm" class="agent-select" placeholder="选择模型">
        <el-option v-for="profile in props.llmProfiles" :key="profile.id" :label="`${profile.name || profile.id} · ${profile.modelId}`" :value="profile.id" />
      </el-select>
      <el-button circle :loading="props.refreshingAgents" aria-label="刷新 Agent 列表" title="刷新 Agent 列表" @click="emit('refresh-agents')">↻</el-button>
      <el-button circle :aria-label="props.themeMode === 'dark' ? '切换浅色主题' : '切换深色主题'" title="切换主题" @click="emit('toggle-theme')">
        <svg v-if="props.themeMode === 'dark'" width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
          <circle cx="8" cy="8" r="3.2" stroke="currentColor" stroke-width="1.4"/>
          <path d="M8 1.5v1.8M8 12.7v1.8M14.5 8h-1.8M3.3 8H1.5M12.6 3.4l-1.3 1.3M4.7 11.3l-1.3 1.3M12.6 12.6l-1.3-1.3M4.7 4.7L3.4 3.4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>
        </svg>
        <svg v-else width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
          <path d="M13.5 9.2A5.6 5.6 0 0 1 6.8 2.5a5.6 5.6 0 1 0 6.7 6.7z" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/>
        </svg>
      </el-button>
      <el-button circle aria-label="设置" title="设置" @click="emit('settings')">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <circle cx="12" cy="12" r="3"/>
          <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
        </svg>
      </el-button>
    </div>
  </header>
  <div class="route-strip">
    <span><i class="route-indicator" />{{ props.selectedAgentId === AUTO_AGENT_ID && props.allowAuto ? '意图 Agent 自动选路' : (activeAgent?.name || props.selectedAgentId || '等待选择 Agent') }}</span>
    <span v-if="activeAgent?.description">{{ activeAgent.description }}</span>
  </div>
</template>
