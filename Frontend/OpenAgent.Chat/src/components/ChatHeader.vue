<script setup lang="ts">
import { computed } from 'vue'
import { AUTO_AGENT_ID, type AgentSummary } from '../types'

const props = defineProps<{
  statusText: string
  agents: AgentSummary[]
  selectedAgentId: string
  refreshingAgents: boolean
  title: string
  themeMode: 'light' | 'dark'
}>()

const emit = defineEmits<{
  'update:selectedAgentId': [value: string]
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
</script>

<template>
  <header class="topbar">
    <div class="topbar-copy"><span class="topbar-kicker">WORKSPACE / CHAT</span><strong>{{ props.title }}</strong></div>
    <div class="topbar-actions">
      <span class="connection-pill"><i :class="{ connected: props.statusText === '已连接' }" />{{ props.statusText }}</span>
      <el-select v-model="selectedAgent" class="agent-select" placeholder="选择 Agent" @change="emit('agent-change')">
        <el-option label="Auto · 意图路由" :value="AUTO_AGENT_ID" />
        <el-option v-for="agent in props.agents" :key="agent.agentId" :label="`${agent.name || agent.agentId}${agent.apiFormat ? ` · ${agent.apiFormat}` : ''}`" :value="agent.agentId" />
      </el-select>
      <el-button circle :loading="props.refreshingAgents" aria-label="刷新 Agent 列表" title="刷新 Agent 列表" @click="emit('refresh-agents')">↻</el-button>
      <el-button circle :aria-label="props.themeMode === 'dark' ? '切换浅色主题' : '切换深色主题'" @click="emit('toggle-theme')">{{ props.themeMode === 'dark' ? '☀' : '☾' }}</el-button>
      <el-button circle aria-label="设置" title="设置" @click="emit('settings')">⚙</el-button>
    </div>
  </header>
  <div class="route-strip">
    <span><i class="route-indicator" />{{ props.selectedAgentId === AUTO_AGENT_ID ? '意图 Agent 自动选路' : (activeAgent?.name || props.selectedAgentId || '等待选择 Agent') }}</span>
    <span v-if="activeAgent?.description">{{ activeAgent.description }}</span>
  </div>
</template>
