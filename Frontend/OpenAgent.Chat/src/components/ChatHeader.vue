<script setup lang="ts">
import { computed } from 'vue'
import type { AgentSummary } from '../types'

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
</script>

<template>
  <header class="topbar">
    <div class="topbar-status"><span class="status-dot" :class="{ connected: props.statusText === '已连接' }" />{{ props.statusText }}<span class="status-caption">工作台</span></div>
    <div class="topbar-actions">
      <el-select v-model="selectedAgent" placeholder="选择 Agent" @change="emit('agent-change')">
        <el-option v-for="agent in props.agents" :key="agent.agentId" :label="agent.name || agent.agentId" :value="agent.agentId" />
      </el-select>
      <el-button class="agent-refresh-button" circle :loading="props.refreshingAgents" aria-label="刷新 Agent 列表" title="刷新 Agent 列表" @click="emit('refresh-agents')">↻</el-button>
      <el-button class="theme-toggle" circle :aria-label="props.themeMode === 'dark' ? '切换浅色主题' : '切换深色主题'" @click="emit('toggle-theme')">{{ props.themeMode === 'dark' ? '☀' : '☾' }}</el-button>
      <el-button type="primary" plain @click="emit('settings')">设置</el-button>
    </div>
  </header>
  <div class="chat-header"><h2>{{ props.title }}</h2></div>
</template>
