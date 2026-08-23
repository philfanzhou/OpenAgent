<script setup lang="ts">
import { computed } from 'vue'
import type { ConversationRecord, CurrentUserContext } from '../types'

const props = defineProps<{
  conversations: ConversationRecord[]
  selectedConversationId?: string
  streamingConversationIds: string[]
  search: string
  loading?: boolean
  statusText: string
  currentUser: CurrentUserContext | null
}>()

const emit = defineEmits<{
  'update:search': [value: string]
  new: []
  settings: []
  logout: []
  refresh: []
  select: [conversation: ConversationRecord]
  delete: [conversation: ConversationRecord]
  'toggle-collapse': []
  'resize-start': [event: PointerEvent]
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

function formatTime(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? '未知时间'
    : new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date)
}
</script>

<template>
  <el-aside width="240px" class="sidebar">
    <div class="brand">
      <span class="brand-mark">
        <svg width="18" height="18" viewBox="0 0 32 32" fill="none" aria-hidden="true">
          <circle cx="10" cy="11" r="2.5" stroke="currentColor" stroke-width="2"/>
          <circle cx="22" cy="11" r="2.5" stroke="currentColor" stroke-width="2"/>
          <circle cx="16" cy="22" r="2.5" stroke="currentColor" stroke-width="2"/>
          <path d="M10 11L16 22M22 11L16 22M10 11L22 11" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" opacity="0.45"/>
        </svg>
      </span>
      <div><strong>OpenAgent</strong><small>Agent workspace</small></div>
    </div>
    <div class="sidebar-toolbar">
      <button class="new-chat-btn" type="button" @click="emit('new')">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
          <path d="M8 3v10M3 8h10" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
        </svg>
        <span>新对话</span>
      </button>
      <button class="sidebar-collapse-btn" type="button" aria-label="收起侧栏" title="收起侧栏" @click="emit('toggle-collapse')">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
          <path d="M10 4L6 8l4 4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>
        </svg>
      </button>
    </div>
    <el-input :model-value="props.search" clearable placeholder="搜索对话" class="search-input" @update:model-value="emit('update:search', $event)" />
    <div class="conversation-heading"><div><span class="section-label">对话</span><strong>{{ filteredConversations.length }}</strong></div><el-button text class="sidebar-refresh" :loading="props.loading" @click="emit('refresh')">同步</el-button></div>
    <el-scrollbar class="conversation-list" wrap-class="conversation-list-wrap">
      <div v-for="group in conversationGroups" :key="group.label" class="conversation-group">
        <div class="conversation-group-label">{{ group.label }}</div>
        <div v-for="item in group.items" :key="item.conversationId" class="conversation-item" :class="{ active: props.selectedConversationId === item.conversationId, streaming: props.streamingConversationIds.includes(item.conversationId) }" @click="emit('select', item)">
          <div class="conversation-icon">{{ (item.title || '新').slice(0, 1) }}</div>
          <div class="conversation-content"><div class="conversation-title">{{ item.title || '未命名会话' }}</div><div class="conversation-meta"><span v-if="props.streamingConversationIds.includes(item.conversationId)" class="conversation-streaming"><i />生成中</span><span v-else>{{ item.agentId || '自动路由' }}</span><time>{{ formatTime(item.updatedAt || item.lastMessageAt) }}</time></div></div>
          <el-button text class="conversation-more" @click.stop="emit('delete', item)">×</el-button>
        </div>
      </div>
      <div v-if="!filteredConversations.length" class="empty-conversations"><div class="empty-orb">✦</div><strong>还没有对话</strong><span>新建一个对话开始吧</span></div>
    </el-scrollbar>
    <footer class="sidebar-footer">
      <button class="identity-button" type="button" @click="emit('settings')">
        <span class="identity-avatar">{{ (props.currentUser?.userId || 'G').slice(0, 1).toUpperCase() }}</span>
        <span class="identity-info"><strong>{{ props.currentUser?.userId || 'Guest' }}</strong><small>{{ props.currentUser?.tenantId || '未设置租户' }}</small></span>
        <i class="status-dot" :class="{ connected: props.statusText === '已连接' }" />
      </button>
      <button class="sidebar-logout" type="button" aria-label="退出登录" title="退出登录" @click="emit('logout')">
        <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true">
          <path d="M9.5 3.5H4v9h5.5" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>
          <path d="M7.5 8h6M11 5.5L13.5 8 11 10.5" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>
        </svg>
      </button>
    </footer>
    <div class="sidebar-resize" @pointerdown="emit('resize-start', $event)" />
  </el-aside>
</template>
