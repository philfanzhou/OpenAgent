<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from './api'
import ChatHeader from './components/ChatHeader.vue'
import ChatMessages from './components/ChatMessages.vue'
import ChatSidebar from './components/ChatSidebar.vue'
import LoginPage from './components/LoginPage.vue'
import MessageComposer from './components/MessageComposer.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import { useAuthentication } from './composables/useAuthentication'
import { useChatStreaming } from './composables/useChatStreaming'
import { useConversationState } from './composables/useConversationState'
import { useConversationStreams } from './composables/useConversationStreams'
import { useFileHandling } from './composables/useFileHandling'
import { usePanelLayout } from './composables/usePanelLayout'
import { useSettings } from './composables/useSettings'
import { formatCacheHitRate, formatTokenCount } from './tokenUsage'
import { AUTO_AGENT_ID, type AgentSummary, type CurrentUserContext } from './types'

const agents = ref<AgentSummary[]>([])
const currentUser = ref<CurrentUserContext | null>(null)
const selectedAgentId = ref(AUTO_AGENT_ID)
const search = ref('')
const workspaceLoading = ref(false)
const themeMode = ref<'light' | 'dark'>(localStorage.getItem('openagent.ui.theme') === 'dark' ? 'dark' : 'light')
const conversationStreams = useConversationStreams()
const { sidebarCollapsed, contextCollapsed, toggleSidebar, toggleContext, startSidebarResize, startContextResize } = usePanelLayout()

const authentication = useAuthentication({
  currentUser,
  loadWorkspace: () => loadWorkspace(),
  resetWorkspace: () => resetWorkspace(),
  cancelStreams: async reason => {
    await Promise.allSettled(conversationStreams.cancelAll(reason))
  },
  closeSettings: () => {
    showSettings.value = false
  },
  notifyError,
})
const {
  connectionMode,
  routerUrl,
  engineUrl,
  tenantId,
  statusText,
  authConfig,
  authLoading,
  authView,
  authError,
  authReason,
  activeEndpointUrl,
  activeEndpointLabel,
  activeEndpointHost,
  connect,
  updateLoginConnection,
  loginWithPassword,
  loginWithOidc,
  returnToWorkspace,
  detectAuthentication,
  logout,
  testHealth,
  initializeAuthentication,
  disposeAuthentication,
} = authentication

const settings = useSettings({
  agents,
  selectedAgentId,
  connectionMode,
  routerUrl,
  engineUrl,
  notifyError,
})
const {
  showSettings,
  refreshingAgents,
  config,
  refreshAgents,
  handleAgentChange,
  openSettings,
  resetSettings,
} = settings

const settingsDialogContext = {
  ...settings,
  connectionMode,
  routerUrl,
  engineUrl,
  tenantId,
  statusText,
  authConfig,
  currentUser,
  agents,
  selectedAgentId,
  activeEndpointLabel,
  activeEndpointHost,
  connect,
  logout,
  testHealth,
}

const files = useFileHandling({
  getSelectedConversation: () => selectedConversation.value,
  notifyError,
})
const {
  pendingFiles,
  handleFilesChange,
  retryPendingFile,
  hydrateFilePreviews,
  downloadFile,
  clearPendingFiles,
  clearMarkdownImageCache,
  markdownImageUrls,
} = files

const conversationState = useConversationState({
  selectedAgentId,
  streams: conversationStreams,
  hydrateFilePreviews,
  notifyError,
  onSelectedConversationDeleted: () => newConversation(),
})
const {
  conversations,
  selectedConversation,
  currentMessages,
  streamingConversationIds,
  loadingConversation,
  selectedConversationStreaming,
  compactingConversation,
  conversationStatusText,
  currentUsageSummary,
  mergeConversationList,
  replaceConversation,
  refreshConversations,
  restoreSelectedConversation,
  selectConversation,
  clearSelectedConversation,
  deleteConversation,
  compactConversation,
  resetConversations,
} = conversationState

const chatStreaming = useChatStreaming({
  selectedAgentId,
  agents,
  conversations,
  selectedConversation,
  selectedConversationStreaming,
  pendingFiles,
  streams: conversationStreams,
  hydrateFilePreviews,
  replaceConversation,
  refreshConversations,
  notifyError,
})
const { message, send, stopStreaming, clearDraft } = chatStreaming

const chatMessagesRef = ref<InstanceType<typeof ChatMessages> | null>(null)

function handleSend(): void {
  // 发送后强制回到底部并恢复自动跟随；流式期间用户上滑阅读不会被拉回。
  chatMessagesRef.value?.scrollToBottom(true)
  void send()
}

const selectedAgent = computed(() => agents.value.find(agent => agent.agentId === selectedAgentId.value) || null)
const routeMode = computed(() => connectionMode.value === 'engine'
  ? 'Engine 直连'
  : selectedAgentId.value === AUTO_AGENT_ID
    ? '自动意图路由'
    : '显式 Agent')

watch(connectionMode, mode => {
  statusText.value = '未连接'
  if (mode === 'engine' && selectedAgentId.value === AUTO_AGENT_ID) {
    selectedAgentId.value = agents.value[0]?.agentId || ''
  }
})

function notifyError(error: unknown): void {
  ElMessage.error(error instanceof Error ? error.message : '请求失败')
}

function applyTheme(): void {
  document.documentElement.dataset.theme = themeMode.value
  // Element Plus dark mode depends on both the data attribute and the dark class.
  document.documentElement.classList.toggle('dark', themeMode.value === 'dark')
  localStorage.setItem('openagent.ui.theme', themeMode.value)
}

function toggleTheme(): void {
  themeMode.value = themeMode.value === 'dark' ? 'light' : 'dark'
  applyTheme()
}

function newConversation(): void {
  clearSelectedConversation()
  clearDraft()
}

function resetWorkspace(): void {
  currentUser.value = null
  agents.value = []
  resetConversations()
  clearPendingFiles()
  clearMarkdownImageCache()
  message.value = ''
  resetSettings()
}

async function loadWorkspace(): Promise<void> {
  workspaceLoading.value = true
  try {
    const [agentResult, conversationResult, userResult] = await Promise.allSettled([
      api.listAgents(),
      api.listConversations(),
      api.getCurrentUser(),
    ])
    if (agentResult.status === 'fulfilled') agents.value = agentResult.value
    else notifyError(agentResult.reason)
    if (conversationResult.status === 'fulfilled') mergeConversationList(conversationResult.value)
    else if (!conversationStreams.activeConversationIds().length) conversations.value = []
    if (userResult.status === 'fulfilled') currentUser.value = userResult.value
    if ((connectionMode.value === 'engine' && selectedAgentId.value === AUTO_AGENT_ID)
      || (selectedAgentId.value !== AUTO_AGENT_ID && !agents.value.some(item => item.agentId === selectedAgentId.value))) {
      selectedAgentId.value = agents.value[0]?.agentId || ''
      config.value = null
    }
    await restoreSelectedConversation()
  } catch (error) {
    notifyError(error)
  } finally {
    workspaceLoading.value = false
  }
}

function handlePageUnload(): void {
  conversationStreams.cancelAll('unload')
}

onMounted(async () => {
  applyTheme()
  window.addEventListener('beforeunload', handlePageUnload)
  await initializeAuthentication()
})

onBeforeUnmount(() => {
  disposeAuthentication()
  window.removeEventListener('beforeunload', handlePageUnload)
  handlePageUnload()
})
</script>

<template>
  <LoginPage v-if="authView === 'login'" :auth-config="authConfig" :loading="authLoading" :error="authError" :reason="authReason" :connection-mode="connectionMode" :router-url="routerUrl" :engine-url="engineUrl" :tenant-id="tenantId" @basic-login="loginWithPassword" @oidc-login="loginWithOidc" @retry="detectAuthentication(false)" @update-connection="updateLoginConnection" />
  <main v-else-if="authView === 'restoring'" class="auth-state-page" aria-live="polite" aria-busy="true"><span class="auth-state-spinner" aria-hidden="true" /><h1>正在恢复登录状态</h1><p>正在安全验证当前会话，请稍候。</p></main>
  <main v-else-if="authView === 'forbidden'" class="auth-state-page"><span class="auth-state-code">403</span><h1>无权访问此内容</h1><p>身份验证有效，但当前账号不具备所请求资源的权限。权限由服务端策略决定。</p><div><el-button @click="returnToWorkspace">返回工作台</el-button><el-button type="primary" @click="logout">退出登录</el-button></div></main>
  <el-container v-else class="app-shell" :class="{ 'sidebar-collapsed': sidebarCollapsed }">
    <ChatSidebar :conversations="conversations" :selected-conversation-id="selectedConversation?.conversationId" :streaming-conversation-ids="streamingConversationIds" :search="search" :loading="workspaceLoading" :status-text="statusText" :current-user="currentUser" @update:search="search = $event" @new="newConversation" @settings="openSettings('gateway')" @logout="logout" @refresh="loadWorkspace" @select="selectConversation" @delete="deleteConversation" @toggle-collapse="toggleSidebar" @resize-start="startSidebarResize" />
    <button v-if="sidebarCollapsed" class="panel-restore sidebar-restore" type="button" aria-label="展开侧栏" title="展开侧栏" @click="toggleSidebar">›</button>

    <el-main class="main-panel">
      <ChatHeader :status-text="statusText" :agents="agents" :selected-agent-id="selectedAgentId" :allow-auto="connectionMode === 'router'" :refreshing-agents="refreshingAgents" :title="selectedConversation?.title || '新对话'" :theme-mode="themeMode" @update:selected-agent-id="selectedAgentId = $event" @agent-change="handleAgentChange" @refresh-agents="refreshAgents" @settings="openSettings('gateway')" @toggle-theme="toggleTheme" />

      <div class="workspace-grid" :class="{ 'context-collapsed': contextCollapsed }">
        <section class="chat-card">
          <ChatMessages ref="chatMessagesRef" :messages="currentMessages" :context-summaries="selectedConversation?.contextSummaries" :loading="loadingConversation" :current-user="currentUser" :streaming="selectedConversationStreaming" :conversation-id="selectedConversation?.conversationId" :markdown-image-urls="markdownImageUrls" @suggest="message = $event" @download="downloadFile" />
          <MessageComposer :model-value="message" :endpoint-url="activeEndpointUrl" :endpoint-label="activeEndpointLabel" :selected-agent-id="selectedAgentId" :loading="selectedConversationStreaming" :pending-files="pendingFiles" @update:model-value="message = $event" @files-change="handleFilesChange" @retry-file="retryPendingFile" @send="handleSend" @stop="stopStreaming" />
        </section>
        <aside class="context-panel">
          <div class="context-panel-head"><span class="context-label">INSPECTOR</span><button class="panel-collapse-btn" type="button" aria-label="收起上下文面板" title="收起" @click="toggleContext">›</button></div>
          <section><span class="context-label">ROUTING</span><strong>{{ routeMode }}</strong><p>{{ connectionMode === 'router' && selectedAgentId === AUTO_AGENT_ID ? '由意图识别 Agent 分析请求并选择目标。' : (selectedAgent?.description || selectedAgentId) }}</p><dl><div><dt>Agent</dt><dd>{{ connectionMode === 'router' && selectedAgentId === AUTO_AGENT_ID ? '由模型选择' : (selectedAgent?.name || selectedAgentId) }}</dd></div><div><dt>协议</dt><dd>{{ selectedAgent?.apiFormat || (connectionMode === 'router' ? '自动' : '—') }}</dd></div></dl></section>
          <section><span class="context-label">IDENTITY</span><dl><div><dt>用户名</dt><dd>{{ currentUser?.username || '未设置' }}</dd></div><div><dt>邮箱</dt><dd :title="currentUser?.email">{{ currentUser?.email || '未设置' }}</dd></div><div><dt>ID</dt><dd :title="currentUser?.userId">{{ currentUser?.userId || 'Guest' }}</dd></div><div><dt>租户</dt><dd>{{ currentUser?.tenantId || tenantId || '—' }}</dd></div><div><dt>{{ activeEndpointLabel }}</dt><dd :title="activeEndpointUrl">{{ activeEndpointHost }}</dd></div></dl></section>
          <section><span class="context-label">CONVERSATION</span><dl><div><dt>消息</dt><dd>{{ currentMessages.length }}</dd></div><div><dt>状态</dt><dd>{{ conversationStatusText }}</dd></div><div><dt>ID</dt><dd class="conversation-id" :title="selectedConversation?.conversationId">{{ selectedConversation?.conversationId || '尚未创建' }}</dd></div></dl><div class="conversation-usage"><div class="token-usage-head"><span>Token</span><span class="token-usage-status" :class="{ unavailable: !currentUsageSummary.available }">{{ currentUsageSummary.available ? (currentUsageSummary.estimated ? '预估' : '完整') : '部分' }}</span></div><template v-if="currentUsageSummary.available && currentUsageSummary.usage"><div class="token-usage-total"><strong>{{ currentUsageSummary.estimated ? '≈' : '' }}{{ formatTokenCount(currentUsageSummary.usage.totalTokens) }}</strong><small>总计</small></div><dl class="token-usage-grid"><div><dt>输入</dt><dd>{{ formatTokenCount(currentUsageSummary.usage.promptTokens) }}</dd></div><div><dt>输出</dt><dd>{{ currentUsageSummary.estimated ? '≈' : '' }}{{ formatTokenCount(currentUsageSummary.usage.completionTokens) }}</dd></div><div><dt>缓存命中</dt><dd>{{ currentUsageSummary.usage.cachedInputTokens != null ? formatTokenCount(currentUsageSummary.usage.cachedInputTokens) : '—' }}</dd></div><div><dt>命中率</dt><dd>{{ formatCacheHitRate(currentUsageSummary.usage.cachedInputTokens, currentUsageSummary.usage.promptTokens) ?? '—' }}</dd></div><div class="grid-span-two"><dt>思考</dt><dd>{{ currentUsageSummary.usage.reasoningTokens != null ? formatTokenCount(currentUsageSummary.usage.reasoningTokens) : '—' }}</dd></div></dl></template><template v-else><strong class="unavailable">—</strong><small>Provider usage 不完整</small></template></div></section>
          <section class="inspector-actions"><span class="context-label">OPERATIONS</span><div class="inspector-action-grid"><el-button class="inspector-action inspector-action-primary" size="small" :loading="compactingConversation" :disabled="!selectedConversation || selectedConversationStreaming" @click="compactConversation"><span class="inspector-action-mark" aria-hidden="true">↻</span><span>手动压缩</span></el-button><el-button class="inspector-action" size="small" @click="openSettings('health')"><span class="inspector-action-mark" aria-hidden="true">✓</span><span>平台健康检查</span></el-button></div></section>
          <div class="context-resize" @pointerdown="startContextResize" />
        </aside>
      </div>
      <button v-if="contextCollapsed" class="panel-restore context-restore" type="button" aria-label="展开上下文面板" title="展开上下文面板" @click="toggleContext">‹</button>
    </el-main>
  </el-container>

  <SettingsDialog :context="settingsDialogContext" />
</template>
