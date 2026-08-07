<script setup lang="ts">
import { computed } from 'vue'
import type { ConversationRecord } from '../types'

const props = defineProps<{
  conversations: ConversationRecord[]
  selectedConversationId?: string
  search: string
  loading?: boolean
}>()

const emit = defineEmits<{
  'update:search': [value: string]
  new: []
  settings: []
  refresh: []
  select: [conversation: ConversationRecord]
  delete: [conversation: ConversationRecord]
}>()

const filteredConversations = computed(() => {
  const keyword = props.search.trim().toLowerCase()
  if (!keyword) return props.conversations
  return props.conversations.filter(item => `${item.title || ''} ${item.conversationId}`.toLowerCase().includes(keyword))
})

const conversationGroups = computed(() => {
  const today = new Date()
  const groups = new Map<string, ConversationRecord[]>()
  for (const item of filteredConversations.value) {
    const date = new Date(item.updatedAt || item.lastMessageAt || item.createdAt)
    const dayDiff = Number.isNaN(date.getTime()) ? 3 : Math.floor((today.getTime() - date.getTime()) / 86400000)
    const label = dayDiff <= 0 ? '今天' : dayDiff === 1 ? '昨天' : '更早'
    if (!groups.has(label)) groups.set(label, [])
    groups.get(label)?.push(item)
  }
  return Array.from(groups, ([label, items]) => ({ label, items }))
})
</script>

<template>
  <el-aside width="310px" class="sidebar">
    <div class="brand"><span class="brand-mark">OA</span><div><strong>OpenAgent</strong><small>Chat Workspace</small></div></div>
    <div class="sidebar-toolbar">
      <el-button type="primary" @click="emit('new')">新建会话</el-button>
      <el-button circle aria-label="打开设置" @click="emit('settings')">⚙</el-button>
    </div>
    <el-input :model-value="props.search" clearable placeholder="搜索会话" class="search-input" @update:model-value="emit('update:search', $event)" />
    <div class="conversation-heading"><div><span class="section-label">最近对话</span><strong>{{ filteredConversations.length }}</strong></div><el-button text class="sidebar-refresh" :loading="props.loading" @click="emit('refresh')">刷新</el-button></div>
    <el-scrollbar class="conversation-list" wrap-class="conversation-list-wrap">
      <div v-for="group in conversationGroups" :key="group.label" class="conversation-group">
        <div class="conversation-group-label">{{ group.label }}</div>
        <div v-for="item in group.items" :key="item.conversationId" class="conversation-item" :class="{ active: props.selectedConversationId === item.conversationId }" @click="emit('select', item)">
          <div class="conversation-icon">{{ (item.title || '新').slice(0, 1) }}</div>
          <div class="conversation-content"><div class="conversation-title">{{ item.title || '未命名会话' }}</div><div class="conversation-id">会话 ID：{{ item.conversationId }}</div></div>
          <el-button text class="conversation-more" @click.stop="emit('delete', item)">×</el-button>
        </div>
      </div>
      <div v-if="!filteredConversations.length" class="empty-conversations"><div class="empty-orb">✦</div><strong>还没有对话</strong><span>新建一个对话开始吧</span></div>
    </el-scrollbar>
  </el-aside>
</template>
