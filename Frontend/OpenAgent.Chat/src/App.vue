<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getAccessToken, getEngineBaseUrl, getTenantId, makeLocalConversation, setAccessToken, setEngineBaseUrl, setTenantId } from './api'
import type { AgentConfigEntity, AgentSummary, ConversationMessage, ConversationRecord, CurrentUserContext, McpServerConfig, McpTestResult, MessageAttachment } from './types'

const engineUrl = ref(getEngineBaseUrl())
const token = ref(getAccessToken())
const tenantId = ref(getTenantId())
const showSettings = ref(!engineUrl.value)
const activeSettings = ref<'engine' | 'agent' | 'mcp' | 'skill'>('engine')
const agents = ref<AgentSummary[]>([])
const currentUser = ref<CurrentUserContext | null>(null)
const conversations = ref<ConversationRecord[]>([])
const selectedConversation = ref<ConversationRecord | null>(null)
const selectedAgentId = ref('')
const message = ref('')
const search = ref('')
const loading = ref(false)
const loadingConversation = ref(false)
const savingConfig = ref(false)
const testingMcp = ref(false)
const testingSkill = ref(false)
const statusText = ref('未连接')
const config = ref<AgentConfigEntity | null>(null)
const mcpDraft = ref<McpServerConfig>({ name: '', url: '', type: 'Http', arguments: [] })
const mcpResult = ref<McpTestResult | null>(null)
const skillResult = ref<Record<string, unknown> | null>(null)
const attachmentInput = ref<HTMLInputElement | null>(null)
const pendingAttachments = ref<Array<{ id: string; file: File }>>([])

const maxAttachmentCount = 5
const maxAttachmentSize = 10 * 1024 * 1024
const maxAttachmentTotalSize = 25 * 1024 * 1024

const filteredConversations = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) return conversations.value
  return conversations.value.filter(item => `${item.title || ''} ${item.agentId || ''}`.toLowerCase().includes(keyword))
})

const currentMessages = computed(() => selectedConversation.value?.messages || [])
const selectedAgent = computed(() => agents.value.find(item => item.agentId === selectedAgentId.value))
const chatSubtitle = computed(() => selectedAgent.value
  ? `${selectedAgent.value.name || selectedAgent.value.agentId} 已准备好为你工作`
  : '选择一个 Agent，开始轻松协作')
const llmJson = computed({
  get: () => config.value ? JSON.stringify(config.value.config.llm, null, 2) : '',
  set: (value: string) => {
    if (!config.value) return
    try { config.value.config.llm = JSON.parse(value) as Record<string, unknown> } catch { /* Keep the editor text parseable before save. */ }
  },
})
const skillsJson = computed({
  get: () => config.value ? JSON.stringify(config.value.config.skills, null, 2) : '',
  set: (value: string) => {
    if (!config.value) return
    try { config.value.config.skills = JSON.parse(value) as { enabledSkills: string[]; instances: Record<string, unknown>[] } } catch { /* Keep the editor text parseable before save. */ }
  },
})
const mcpArgumentsText = computed({
  get: () => (mcpDraft.value.arguments || []).join('\n'),
  set: (value: string) => { mcpDraft.value.arguments = value.split('\n').map(item => item.trim()).filter(Boolean) },
})

function notifyError(error: unknown): void {
  ElMessage.error(error instanceof Error ? error.message : '请求失败')
}

async function connect(): Promise<void> {
  setEngineBaseUrl(engineUrl.value)
  setAccessToken(token.value)
  setTenantId(tenantId.value)
  try {
    await api.health('/ready')
    statusText.value = '已连接'
    showSettings.value = false
    await loadWorkspace()
  } catch (error) {
    statusText.value = '连接失败'
    notifyError(error)
  }
}

async function loadWorkspace(): Promise<void> {
  loading.value = true
  try {
    const [agentItems, conversationItems, userContext] = await Promise.all([api.listAgents(), api.listConversations(), api.getCurrentUser()])
    agents.value = agentItems
    conversations.value = conversationItems
    currentUser.value = userContext
    if (!selectedAgentId.value && agents.value.length) selectedAgentId.value = agents.value[0].agentId
  } catch (error) {
    notifyError(error)
  } finally {
    loading.value = false
  }
}

async function selectConversation(item: ConversationRecord): Promise<void> {
  selectedConversation.value = item
  selectedAgentId.value = item.agentId || selectedAgentId.value
  if (item.messages?.length) return
  loadingConversation.value = true
  try {
    selectedConversation.value = await api.getConversation(item.conversationId)
  } catch (error) {
    notifyError(error)
  } finally {
    loadingConversation.value = false
  }
}

function newConversation(): void {
  selectedConversation.value = null
  message.value = ''
  pendingAttachments.value = []
}

function formatFileSize(size: number): string {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${Math.ceil(size / 1024)} KB`
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}

function openAttachmentPicker(): void {
  attachmentInput.value?.click()
}

function handleAttachmentChange(event: Event): void {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files || [])
  input.value = ''
  if (!files.length) return

  const currentSize = pendingAttachments.value.reduce((total, item) => total + item.file.size, 0)
  const availableCount = maxAttachmentCount - pendingAttachments.value.length
  if (availableCount <= 0) {
    notifyError(`最多上传 ${maxAttachmentCount} 个文件`)
    return
  }

  const accepted: Array<{ id: string; file: File }> = []
  let totalSize = currentSize
  for (const file of files.slice(0, availableCount)) {
    if (file.size > maxAttachmentSize) {
      notifyError(`${file.name} 超过单文件 ${formatFileSize(maxAttachmentSize)} 限制`)
      continue
    }
    if (totalSize + file.size > maxAttachmentTotalSize) {
      notifyError(`附件总大小不能超过 ${formatFileSize(maxAttachmentTotalSize)}`)
      break
    }
    totalSize += file.size
    accepted.push({ id: crypto.randomUUID(), file })
  }
  pendingAttachments.value = [...pendingAttachments.value, ...accepted]
}

function removeAttachment(id: string): void {
  pendingAttachments.value = pendingAttachments.value.filter(item => item.id !== id)
}

async function send(): Promise<void> {
  const content = message.value.trim()
  if (!content || !selectedAgentId.value || loading.value) return
  const attachments = pendingAttachments.value.map(item => item.file)
  const local = selectedConversation.value || makeLocalConversation(selectedAgentId.value, content)
  if (!selectedConversation.value) selectedConversation.value = local
  const conversationId = selectedConversation.value.conversationId
  selectedConversation.value.messages ||= []
  if (selectedConversation.value.messages.length === 0 || selectedConversation.value.messages.at(-1)?.role !== 'user') {
    selectedConversation.value.messages.push({
      messageId: crypto.randomUUID(), sequence: selectedConversation.value.messages.length + 1,
      role: 'user', content, timestamp: new Date().toISOString(),
      attachments: pendingAttachments.value.map(item => ({
        fileName: item.file.name,
        mediaType: item.file.type || 'application/octet-stream',
        length: item.file.size,
      } satisfies MessageAttachment)),
    })
  }
  message.value = ''
  pendingAttachments.value = []
  loading.value = true
  let assistant = ''
  const assistantMessage: ConversationMessage = {
    messageId: crypto.randomUUID(), sequence: selectedConversation.value.messages.length + 1,
    role: 'assistant', content: '', timestamp: new Date().toISOString(),
  }
  selectedConversation.value.messages.push(assistantMessage)
  try {
    for await (const event of api.streamChat(content, selectedAgentId.value, conversationId, attachments)) {
      if (event.type === 'content') {
        assistant += event.content || ''
        assistantMessage.content = assistant
      } else if (event.type === 'tool_call') {
        assistantMessage.toolName = event.toolName
      } else if (event.type === 'error') {
        throw new Error(event.error?.detail || 'Agent 执行失败')
      }
    }
    if (!conversationId) await loadWorkspace()
  } catch (error) {
    assistantMessage.content = error instanceof Error ? error.message : '执行失败'
    notifyError(error)
  } finally {
    loading.value = false
  }
}

async function deleteConversation(item: ConversationRecord): Promise<void> {
  try {
    await ElMessageBox.confirm('确认删除这个会话吗？', '删除会话', { type: 'warning' })
    await api.deleteConversation(item.conversationId)
    conversations.value = conversations.value.filter(value => value.conversationId !== item.conversationId)
    if (selectedConversation.value?.conversationId === item.conversationId) newConversation()
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function loadConfig(): Promise<void> {
  if (!selectedAgentId.value) return
  try {
    config.value = await api.getAgentConfig(selectedAgentId.value)
  } catch (error) {
    notifyError(error)
  }
}

function createDefaultAgent(agentId: string, name: string): AgentConfigEntity {
  return {
    agentId,
    name,
    status: 0,
    currentVersion: '',
    config: {
      llm: {
        provider: '',
        format: 'OpenAIChatCompletions',
        modelId: 'gpt-4o',
        apiKey: '',
        endpoint: '',
        temperature: 0.7,
      },
      mcp: { servers: [] },
      rag: { enabled: false, enabledRagInstanceIds: [], instances: [] },
      skills: { enabledSkills: [], instances: [] },
      maxTurns: 50,
    },
  }
}

async function createAgent(): Promise<void> {
  try {
    const result = await ElMessageBox.prompt('请输入 Agent ID（例如 customer-support）', '新增 Agent', {
      confirmButtonText: '创建',
      cancelButtonText: '取消',
      inputPattern: /^[a-zA-Z0-9][a-zA-Z0-9._-]*$/,
      inputErrorMessage: '只能使用字母、数字、点、下划线或短横线',
    })
    const agentId = result.value.trim()
    const created = await api.saveAgentConfig(agentId, createDefaultAgent(agentId, agentId))
    config.value = created
    selectedAgentId.value = agentId
    agents.value = [
      ...agents.value.filter(item => item.agentId !== agentId),
      { agentId, name: created.name, status: created.status, currentVersion: created.currentVersion, apiFormat: String(created.config.llm.format || '') },
    ]
    ElMessage.success('Agent 已创建，请补充 LLM 配置')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function saveConfig(): Promise<void> {
  if (!config.value) return
  savingConfig.value = true
  try {
    config.value = await api.saveAgentConfig(config.value.agentId, config.value)
    ElMessage.success('配置已保存')
  } catch (error) {
    notifyError(error)
  } finally {
    savingConfig.value = false
  }
}

async function saveMcp(): Promise<void> {
  if (!selectedAgentId.value || !mcpDraft.value.name.trim()) return
  try {
    await api.saveMcp(mcpDraft.value.name.trim(), selectedAgentId.value, mcpDraft.value)
    ElMessage.success('MCP 配置已保存')
  } catch (error) { notifyError(error) }
}

async function saveSkills(): Promise<void> {
  if (!selectedAgentId.value || !config.value) return
  try {
    config.value.config.skills = await api.saveSkills(selectedAgentId.value, config.value.config.skills)
    ElMessage.success('Skill 配置已保存')
  } catch (error) { notifyError(error) }
}

async function testSkills(): Promise<void> {
  if (!config.value) return
  testingSkill.value = true
  try { skillResult.value = await api.testSkills(config.value.config.skills) } catch (error) { notifyError(error) } finally { testingSkill.value = false }
}

async function testMcp(): Promise<void> {
  testingMcp.value = true
  try {
    mcpResult.value = await api.testMcp(mcpDraft.value, selectedAgentId.value)
  } catch (error) {
    notifyError(error)
  } finally {
    testingMcp.value = false
  }
}

function openSettings(panel: typeof activeSettings.value): void {
  activeSettings.value = panel
  showSettings.value = true
  if (panel === 'agent') void loadConfig()
}

function handleSettingsTabChange(name: string | number): void {
  if (name === 'agent' || name === 'skill') void loadConfig()
}

onMounted(() => {
  if (engineUrl.value) void connect()
})
</script>

<template>
  <el-container class="app-shell">
    <el-aside width="310px" class="sidebar">
      <div class="brand"><span class="brand-mark">OA</span><div><strong>OpenAgent</strong><small>Chat Workspace</small></div></div>
      <div class="sidebar-toolbar">
        <el-button type="primary" @click="newConversation">新建会话</el-button>
        <el-button circle @click="openSettings('engine')">⚙</el-button>
      </div>
      <el-input v-model="search" clearable placeholder="搜索会话" class="search-input" />
      <div class="section-label">会话 · {{ filteredConversations.length }}</div>
      <el-scrollbar class="conversation-list">
        <div v-for="item in filteredConversations" :key="item.conversationId" class="conversation-item" :class="{ active: selectedConversation?.conversationId === item.conversationId }" @click="selectConversation(item)">
          <div class="conversation-title">{{ item.title || '未命名会话' }}</div>
          <div class="conversation-meta"><span>{{ item.agentId || '未选择 Agent' }}</span><el-button link type="danger" @click.stop="deleteConversation(item)">删除</el-button></div>
        </div>
        <el-empty v-if="!filteredConversations.length" description="暂无会话" />
      </el-scrollbar>
    </el-aside>

    <el-main class="main-panel">
      <header class="topbar">
        <div class="topbar-status"><span class="status-dot" :class="{ connected: statusText === '已连接' }" />{{ statusText }}<span class="status-caption">工作台</span></div>
        <div class="topbar-actions">
          <el-select v-model="selectedAgentId" placeholder="选择 Agent" @change="newConversation">
            <el-option v-for="agent in agents" :key="agent.agentId" :label="agent.name || agent.agentId" :value="agent.agentId" />
          </el-select>
          <el-button type="primary" plain @click="openSettings('engine')">设置</el-button>
        </div>
      </header>

      <section class="chat-card">
        <div class="chat-header"><div><span class="chat-kicker">OPENAGENT CHAT</span><h2>{{ selectedConversation?.title || '今天想从哪里开始？' }}</h2><p>{{ chatSubtitle }}</p></div><el-button text @click="newConversation">清空当前</el-button></div>
        <el-scrollbar class="messages" v-loading="loadingConversation">
          <div v-if="!currentMessages.length" class="welcome"><div class="welcome-orbit"><div class="welcome-icon">✦</div><span class="orbit-dot orbit-dot-one" /><span class="orbit-dot orbit-dot-two" /></div><h1>你好，今天想完成什么？</h1><p>把问题、文件或灵感交给你的 Agent，一起把事情做好。</p></div>
          <div v-for="item in currentMessages" :key="item.messageId" class="message-row" :class="item.role">
            <div class="avatar">{{ item.role === 'user' ? '我' : item.role === 'tool' ? '工具' : 'AI' }}</div>
            <div class="message-bubble"><div v-if="item.toolName" class="tool-tag">调用工具：{{ item.toolName }}</div><div v-if="item.attachments?.length" class="message-attachments"><span v-for="attachment in item.attachments" :key="attachment.fileName">↗ {{ attachment.fileName }}</span></div><div class="message-content">{{ item.content || '…' }}</div></div>
          </div>
        </el-scrollbar>
        <div class="composer">
          <div v-if="pendingAttachments.length" class="attachment-list">
            <div v-for="item in pendingAttachments" :key="item.id" class="attachment-chip">
              <span class="attachment-icon">↗</span><span class="attachment-name" :title="item.file.name">{{ item.file.name }}</span><span class="attachment-size">{{ formatFileSize(item.file.size) }}</span>
              <el-button link class="attachment-remove" @click="removeAttachment(item.id)">×</el-button>
            </div>
          </div>
          <el-input v-model="message" type="textarea" :rows="3" resize="none" placeholder="输入消息，按 Ctrl/⌘ + Enter 发送" @keydown="(event: KeyboardEvent) => { if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') send() }" />
          <input ref="attachmentInput" class="attachment-input" type="file" multiple accept=".png,.jpg,.jpeg,.gif,.webp,.pdf,.json,.txt,.csv,.md" @change="handleAttachmentChange" />
          <div class="composer-footer"><div class="composer-hints"><el-button text class="attach-button" @click="openAttachmentPicker">＋ 添加附件</el-button><span>支持图片、PDF、JSON、TXT、CSV、Markdown</span></div><div class="composer-actions"><span>Engine：{{ engineUrl || '未配置' }}</span><el-button type="primary" :loading="loading" :disabled="!selectedAgentId || !message.trim()" @click="send">发送</el-button></div></div>
        </div>
      </section>
    </el-main>
  </el-container>

  <el-dialog v-model="showSettings" class="settings-dialog" width="min(960px, calc(100vw - 32px))" top="5vh" :close-on-click-modal="false" destroy-on-close>
    <template #header>
      <div class="settings-header"><div><span class="eyebrow">WORKSPACE CONTROL</span><h2>工作台设置</h2></div><el-tag v-if="selectedAgent" effect="plain" round>{{ selectedAgent.name || selectedAgent.agentId }}</el-tag></div>
    </template>
    <div class="settings-body">
      <el-tabs v-model="activeSettings" tab-position="left" class="settings-tabs" @tab-change="handleSettingsTabChange">
        <el-tab-pane label="Engine 连接" name="engine">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CONNECTION</span><h3>连接与身份</h3><p>配置当前工作台要访问的 Engine，设置会保存在本机浏览器中。</p></div><span class="connection-badge" :class="{ online: statusText === '已连接' }"><i />{{ statusText }}</span></div>
            <el-form label-position="top"><el-form-item label="Engine 地址"><el-input v-model="engineUrl" placeholder="http://localhost:5208" /></el-form-item><el-form-item label="Bearer Token"><el-input v-model="token" type="password" show-password placeholder="可选：从认证中心获取的 Access Token" /></el-form-item><el-form-item label="租户 ID"><el-input v-model="tenantId" placeholder="由认证中心 Token 中的 tid 决定" /></el-form-item></el-form>
            <el-descriptions :column="2" border class="identity-status"><el-descriptions-item label="当前用户">{{ currentUser?.userId || '未连接' }}</el-descriptions-item><el-descriptions-item label="当前租户">{{ currentUser?.tenantId || tenantId || '未识别' }}</el-descriptions-item><el-descriptions-item label="认证状态">{{ currentUser?.isAuthenticated ? '已认证' : '未认证' }}</el-descriptions-item><el-descriptions-item label="Token 状态">{{ token ? '已配置（Bearer）' : '未配置' }}</el-descriptions-item></el-descriptions>
            <el-alert title="当前页面直接调用 Engine，账号密码和 Microsoft/企业 SSO 由外部认证中心负责。" type="info" :closable="false" /><div class="button-row"><el-button type="primary" @click="connect">保存并连接</el-button><el-button @click="api.health('/health').then(() => ElMessage.success('Live 健康检查通过')).catch(notifyError)">测试 Live</el-button><el-button @click="api.health('/ready').then(() => ElMessage.success('Ready 健康检查通过')).catch(notifyError)">测试 Ready</el-button></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Agent 配置" name="agent">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">AGENT RUNTIME</span><h3>Agent 配置</h3><p>模型、上下文轮次以及绑定的 Skill 都在这里统一管理。</p></div><div class="section-actions"><el-button type="primary" plain @click="createAgent">新增 Agent</el-button><el-button v-if="selectedAgentId" @click="loadConfig">重新加载</el-button></div></div>
            <el-empty v-if="!config" description="请先选择或新增 Agent" /><template v-else><el-form label-position="top"><el-form-item label="名称"><el-input v-model="config.name" /></el-form-item><el-form-item label="最大轮次"><el-input-number v-model="config.config.maxTurns" :min="1" :max="1000" /></el-form-item><el-form-item label="LLM 配置 JSON"><el-input v-model="llmJson" type="textarea" :rows="8" /></el-form-item></el-form><el-button type="primary" :loading="savingConfig" @click="saveConfig">保存 Agent 配置</el-button></template>
          </section>
        </el-tab-pane>
        <el-tab-pane label="MCP 绑定" name="mcp">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>MCP 绑定</h3><p>连接由 Engine 服务端执行，本页面只保存配置并发起测试。</p></div></div>
            <el-form label-position="top"><el-form-item label="名称"><el-input v-model="mcpDraft.name" placeholder="例如 local-tools" /></el-form-item><el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="HTTP" value="Http" /><el-option label="SSE" value="SSE" /><el-option label="Stdio（服务端执行）" value="Stdio" /></el-select></el-form-item><el-form-item v-if="mcpDraft.type !== 'Stdio'" label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com" /></el-form-item><template v-else><el-form-item label="Command"><el-input v-model="mcpDraft.command" placeholder="例如 node" /></el-form-item><el-form-item label="Arguments（每行一个）"><el-input v-model="mcpArgumentsText" type="textarea" :rows="3" /></el-form-item><el-form-item label="Working Directory"><el-input v-model="mcpDraft.workingDirectory" /></el-form-item></template><div class="button-row"><el-button type="primary" :loading="testingMcp" @click="testMcp">测试连接与权限</el-button><el-button :disabled="!selectedAgentId || !mcpDraft.name" @click="saveMcp">保存 MCP 配置</el-button></div></el-form><pre v-if="mcpResult" class="result-box">{{ JSON.stringify(mcpResult, null, 2) }}</pre>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Skill 绑定" name="skill">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>Skill 绑定</h3><p>按 Agent 维护 Skill 可见性、实例和权限范围。</p></div></div>
            <el-empty v-if="!config" description="请先选择或新增 Agent" /><template v-else><el-alert title="Skill 可见性和实例权限由服务端统一校验。" type="info" :closable="false" /><el-form label-position="top"><el-form-item label="Skill 配置 JSON"><el-input v-model="skillsJson" type="textarea" :rows="12" /></el-form-item></el-form><div class="button-row"><el-button type="primary" @click="saveSkills">保存 Skill 配置</el-button><el-button :loading="testingSkill" @click="testSkills">测试 Skill 配置</el-button></div><pre v-if="skillResult" class="result-box">{{ JSON.stringify(skillResult, null, 2) }}</pre></template>
          </section>
        </el-tab-pane>
      </el-tabs>
    </div>
  </el-dialog>
</template>

<style>
.message-attachments { display: flex; flex-wrap: wrap; gap: 5px; margin-bottom: 7px; }
.message-attachments span { display: inline-flex; align-items: center; padding: 4px 7px; color: #5d54e8; background: #fff; border: 1px solid #dfe2ff; border-radius: 6px; font-size: 11px; }
</style>
