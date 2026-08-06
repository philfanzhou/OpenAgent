<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getAccessToken, getEngineBaseUrl, getTenantId, makeLocalConversation, setAccessToken, setEngineBaseUrl, setTenantId } from './api'
import type { AgentConfigEntity, AgentSummary, AuthConfig, ConversationMessage, ConversationRecord, CurrentUserContext, McpServerConfig, McpTestResult, MessageAttachment, RagConfig, RagInstanceConfig, RagTestResult, SkillInstanceConfig, SkillsConfig } from './types'

const engineUrl = ref(getEngineBaseUrl())
const token = ref(getAccessToken())
const tenantId = ref(getTenantId())
const showSettings = ref(!engineUrl.value)
const activeSettings = ref<'engine' | 'agent' | 'mcp' | 'skill' | 'rag'>('engine')
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
const testingRag = ref(false)
const statusText = ref('未连接')
const config = ref<AgentConfigEntity | null>(null)
const authConfig = ref<AuthConfig | null>(null)
const loginMethod = ref<'password' | 'microsoft'>('password')
const username = ref('')
const password = ref('')
const authLoading = ref(false)
const showAgentEditor = ref(false)
const showMcpEditor = ref(false)
const showSkillEditor = ref(false)
const showRagEditor = ref(false)
const mcpDraft = ref<McpServerConfig>({ name: '', url: '', type: 'Http', arguments: [] })
const mcpServers = ref<McpServerConfig[]>([])
const selectedMcpIndex = ref(-1)
const mcpResult = ref<McpTestResult | null>(null)
const skillResult = ref<Record<string, unknown> | null>(null)
const skillDraft = ref<SkillsConfig>({ enabledSkills: [], instances: [] })
const selectedSkillIndex = ref(-1)
const ragDraft = ref<RagInstanceConfig>({ id: '', name: '', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: '', apiKey: '' })
const ragInstances = ref<RagInstanceConfig[]>([])
const selectedRagIndex = ref(-1)
const ragResult = ref<RagTestResult | null>(null)
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

const currentMessages = computed(() => selectedConversation.value?.messages || [])
const selectedAgent = computed(() => agents.value.find(item => item.agentId === selectedAgentId.value))
const selectedSkill = computed(() => selectedSkillIndex.value >= 0 ? skillDraft.value.instances[selectedSkillIndex.value] || null : null)
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
const enabledSkillText = computed({
  get: () => skillDraft.value.enabledSkills.join(', '),
  set: (value: string) => { skillDraft.value.enabledSkills = value.split(',').map(item => item.trim()).filter(Boolean) },
})
const mcpArgumentsText = computed({
  get: () => (mcpDraft.value.arguments || []).join('\n'),
  set: (value: string) => { mcpDraft.value.arguments = value.split('\n').map(item => item.trim()).filter(Boolean) },
})

const ragEnabledText = computed(() => config.value?.config.rag?.enabled ? '已启用' : '未启用')

function notifyError(error: unknown): void {
  ElMessage.error(error instanceof Error ? error.message : '请求失败')
}

async function connect(): Promise<void> {
  setEngineBaseUrl(engineUrl.value)
  setAccessToken(token.value)
  setTenantId(tenantId.value)
  try {
    await api.health('/ready')
    await loadAuthConfig()
    statusText.value = '已连接'
    showSettings.value = false
    await loadWorkspace()
  } catch (error) {
    statusText.value = '连接失败'
    notifyError(error)
  }
}

async function loadAuthConfig(): Promise<void> {
  try {
    authConfig.value = await api.getAuthConfig()
    if (!authConfig.value.password.enabled && authConfig.value.microsoft.enabled) loginMethod.value = 'microsoft'
  } catch {
    authConfig.value = null
  }
}

async function loginWithPassword(): Promise<void> {
  if (!username.value.trim() || !password.value) return notifyError(new Error('请输入账号和密码'))
  authLoading.value = true
  try {
    const result = await api.passwordLogin(username.value.trim(), password.value)
    setAccessToken(result.access_token)
    token.value = result.access_token
    password.value = ''
    await connect()
  } catch (error) {
    notifyError(error)
  } finally {
    authLoading.value = false
  }
}

function toBase64Url(bytes: Uint8Array): string {
  let binary = ''
  bytes.forEach(byte => { binary += String.fromCharCode(byte) })
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

async function startMicrosoftLogin(): Promise<void> {
  const microsoft = authConfig.value?.microsoft
  if (!microsoft?.enabled) return notifyError(new Error('Microsoft 登录尚未由 Engine 配置启用'))
  const verifier = toBase64Url(crypto.getRandomValues(new Uint8Array(32)))
  const challenge = toBase64Url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))))
  const state = toBase64Url(crypto.getRandomValues(new Uint8Array(24)))
  const redirectUri = microsoft.redirectUri || `${window.location.origin}/auth/callback`
  sessionStorage.setItem('openagent.auth.pkce.verifier', verifier)
  sessionStorage.setItem('openagent.auth.pkce.state', state)
  const authority = microsoft.authority.replace(/\/$/, '')
  const authorizationEndpoint = microsoft.authorizationEndpoint || `${authority}/oauth2/v2.0/authorize`
  const authorizeUrl = `${authorizationEndpoint}?${new URLSearchParams({
    client_id: microsoft.clientId,
    response_type: 'code',
    redirect_uri: redirectUri,
    response_mode: 'query',
    scope: microsoft.scopes.join(' '),
    state,
    code_challenge: challenge,
    code_challenge_method: 'S256',
  }).toString()}`
  window.location.assign(authorizeUrl)
}

async function completeMicrosoftLogin(): Promise<void> {
  const params = new URLSearchParams(window.location.search)
  const code = params.get('code')
  const state = params.get('state')
  const expectedState = sessionStorage.getItem('openagent.auth.pkce.state')
  const verifier = sessionStorage.getItem('openagent.auth.pkce.verifier')
  if (!code || !state || !expectedState || state !== expectedState || !verifier) return
  authLoading.value = true
  try {
    const redirectUri = authConfig.value?.microsoft.redirectUri || `${window.location.origin}/auth/callback`
    const result = await api.exchangeMicrosoftCode(code, verifier, redirectUri)
    setAccessToken(result.access_token)
    token.value = result.access_token
    sessionStorage.removeItem('openagent.auth.pkce.state')
    sessionStorage.removeItem('openagent.auth.pkce.verifier')
    window.history.replaceState({}, document.title, window.location.pathname)
    await connect()
  } catch (error) {
    notifyError(error)
  } finally {
    authLoading.value = false
  }
}

async function loadWorkspace(): Promise<void> {
  loading.value = true
  try {
    const [agentResult, conversationResult, userResult] = await Promise.allSettled([
      api.listAgents(),
      api.listConversations(),
      api.getCurrentUser(),
    ])
    if (agentResult.status === 'fulfilled') agents.value = agentResult.value
    else notifyError(agentResult.reason)
    if (conversationResult.status === 'fulfilled') conversations.value = conversationResult.value
    else conversations.value = []
    if (userResult.status === 'fulfilled') currentUser.value = userResult.value
    if (!agents.value.some(item => item.agentId === selectedAgentId.value)) {
      selectedAgentId.value = agents.value[0]?.agentId || ''
      config.value = null
    }
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

function handleAgentChange(): void {
  newConversation()
  config.value = null
  mcpServers.value = []
  selectedMcpIndex.value = -1
  skillDraft.value = { enabledSkills: [], instances: [] }
  selectedSkillIndex.value = -1
  ragInstances.value = []
  selectedRagIndex.value = -1
}

function selectAgent(agentId: string): void {
  if (selectedAgentId.value === agentId) return
  selectedAgentId.value = agentId
  handleAgentChange()
  void loadConfig()
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
    skillDraft.value = {
      enabledSkills: [...config.value.config.skills.enabledSkills],
      instances: config.value.config.skills.instances.map(item => ({ ...item })),
    }
    selectedSkillIndex.value = skillDraft.value.instances.length ? 0 : -1
    ragInstances.value = (config.value.config.rag?.instances || []).map(item => ({ ...item }))
    selectedRagIndex.value = ragInstances.value.length ? 0 : -1
    if (selectedRagIndex.value >= 0) selectRag(selectedRagIndex.value)
  } catch (error) {
    notifyError(error)
  }
}

function createDefaultMcp(): McpServerConfig {
  return { name: '', url: '', type: 'Http', arguments: [], environmentVariables: {} }
}

function selectMcp(index: number): void {
  const server = mcpServers.value[index]
  if (!server) return
  selectedMcpIndex.value = index
  mcpDraft.value = { ...server, arguments: [...(server.arguments || [])], environmentVariables: { ...(server.environmentVariables || {}) } }
}

function newMcp(): void {
  selectedMcpIndex.value = -1
  mcpDraft.value = createDefaultMcp()
  mcpResult.value = null
  showMcpEditor.value = true
}

async function editAgent(agentId: string): Promise<void> {
  selectedAgentId.value = agentId
  handleAgentChange()
  await loadConfig()
  showAgentEditor.value = true
}

async function loadMcp(): Promise<void> {
  if (!selectedAgentId.value) return
  try {
    const result = await api.getMcpConfig(selectedAgentId.value)
    mcpServers.value = result.servers || []
    if (selectedMcpIndex.value >= 0 && selectedMcpIndex.value < mcpServers.value.length) selectMcp(selectedMcpIndex.value)
    else if (mcpServers.value.length) selectMcp(0)
    else newMcp()
  } catch (error) {
    notifyError(error)
  }
}

async function deleteMcp(): Promise<void> {
  const current = mcpServers.value[selectedMcpIndex.value]
  if (!current || !selectedAgentId.value) return
  try {
    await ElMessageBox.confirm(`确认移除 MCP「${current.name}」吗？`, '移除 MCP', { type: 'warning' })
    await api.deleteMcp(current.name, selectedAgentId.value)
    mcpServers.value.splice(selectedMcpIndex.value, 1)
    if (mcpServers.value.length) selectMcp(Math.min(selectedMcpIndex.value, mcpServers.value.length - 1))
    else newMcp()
    ElMessage.success('MCP 已移除')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function testMcpRow(index: number): Promise<void> {
  selectMcp(index)
  await testMcp()
  showMcpEditor.value = true
}

function createDefaultSkill(): SkillInstanceConfig {
  return { skillId: '', name: '', enabled: true, description: '', source: 'Local' }
}

function selectSkill(index: number): void {
  if (skillDraft.value.instances[index]) selectedSkillIndex.value = index
}

function newSkill(): void {
  skillDraft.value.instances.push(createDefaultSkill())
  selectedSkillIndex.value = skillDraft.value.instances.length - 1
  showSkillEditor.value = true
}

function editSkill(index: number): void {
  selectSkill(index)
  showSkillEditor.value = true
}

function removeSkill(): void {
  if (selectedSkillIndex.value < 0) return
  const removed = skillDraft.value.instances.splice(selectedSkillIndex.value, 1)[0]
  skillDraft.value.enabledSkills = skillDraft.value.enabledSkills.filter(item => item !== removed?.skillId)
  selectedSkillIndex.value = skillDraft.value.instances.length ? Math.min(selectedSkillIndex.value, skillDraft.value.instances.length - 1) : -1
}

async function testSkillRow(index: number): Promise<void> {
  selectSkill(index)
  await testSkills()
  showSkillEditor.value = true
}

function createDefaultRag(): RagInstanceConfig {
  return { id: '', name: '', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: '', apiKey: '' }
}

function selectRag(index: number): void {
  const instance = ragInstances.value[index]
  if (!instance) return
  selectedRagIndex.value = index
  ragDraft.value = { ...instance, adapterConfig: { ...(instance.adapterConfig || {}) } }
}

function newRag(): void {
  selectedRagIndex.value = -1
  ragDraft.value = createDefaultRag()
  ragResult.value = null
  showRagEditor.value = true
}

function editRag(index: number): void {
  selectRag(index)
  showRagEditor.value = true
}

async function saveRag(): Promise<void> {
  if (!selectedAgentId.value || !ragDraft.value.id.trim()) return
  try {
    const saved = await api.saveRag(ragDraft.value.id.trim(), selectedAgentId.value, ragDraft.value)
    const existingIndex = ragInstances.value.findIndex(item => item.id === saved.id)
    if (existingIndex >= 0) ragInstances.value[existingIndex] = saved
    else ragInstances.value.push(saved)
    if (config.value) {
      config.value.config.rag = {
        ...(config.value.config.rag || { enabled: false, enabledRagInstanceIds: [], instances: [] }),
        instances: ragInstances.value.map(item => ({ ...item })),
        enabledRagInstanceIds: ragInstances.value.filter(item => item.enabled).map(item => item.id),
      }
    }
    selectRag(existingIndex >= 0 ? existingIndex : ragInstances.value.length - 1)
    showRagEditor.value = false
    ElMessage.success('RAG 配置已保存')
  } catch (error) { notifyError(error) }
}

async function deleteRag(): Promise<void> {
  const current = ragInstances.value[selectedRagIndex.value]
  if (!current || !selectedAgentId.value) return
  try {
    await ElMessageBox.confirm(`确认移除 RAG「${current.name || current.id}」吗？`, '移除 RAG', { type: 'warning' })
    await api.deleteRag(current.id, selectedAgentId.value)
    ragInstances.value.splice(selectedRagIndex.value, 1)
    selectedRagIndex.value = ragInstances.value.length ? 0 : -1
    if (selectedRagIndex.value >= 0) selectRag(selectedRagIndex.value)
    ElMessage.success('RAG 已移除')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function testRag(): Promise<void> {
  testingRag.value = true
  try { ragResult.value = await api.testRag(ragDraft.value) } catch (error) { notifyError(error) } finally { testingRag.value = false }
}

async function testRagRow(index: number): Promise<void> {
  selectRag(index)
  await testRag()
  showRagEditor.value = true
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
    showAgentEditor.value = true
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
    const saved = await api.saveMcp(mcpDraft.value.name.trim(), selectedAgentId.value, mcpDraft.value)
    const existingIndex = mcpServers.value.findIndex(item => item.name === saved.name)
    if (existingIndex >= 0) mcpServers.value[existingIndex] = saved
    else mcpServers.value.push(saved)
    selectMcp(existingIndex >= 0 ? existingIndex : mcpServers.value.length - 1)
    showMcpEditor.value = false
    ElMessage.success('MCP 配置已保存')
  } catch (error) { notifyError(error) }
}

async function saveSkills(): Promise<void> {
  if (!selectedAgentId.value || !config.value) return
  try {
    config.value.config.skills = await api.saveSkills(selectedAgentId.value, {
      enabledSkills: [...skillDraft.value.enabledSkills],
      instances: skillDraft.value.instances.map(item => ({ ...item })),
    })
    showSkillEditor.value = false
    ElMessage.success('Skill 配置已保存')
  } catch (error) { notifyError(error) }
}

async function testSkills(): Promise<void> {
  if (!config.value) return
  testingSkill.value = true
  try { skillResult.value = await api.testSkills(skillDraft.value) } catch (error) { notifyError(error) } finally { testingSkill.value = false }
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
  if (panel === 'agent' || panel === 'skill' || panel === 'rag') void loadConfig()
  if (panel === 'mcp') void loadMcp()
}

function handleSettingsTabChange(name: string | number): void {
  if (name === 'agent' || name === 'skill' || name === 'rag') void loadConfig()
  if (name === 'mcp') void loadMcp()
}

onMounted(() => {
  if (engineUrl.value) {
    void (async () => {
      await connect()
      await completeMicrosoftLogin()
    })()
  }
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
      <div class="conversation-heading"><div><span class="section-label">最近对话</span><strong>{{ filteredConversations.length }}</strong></div><el-button text class="sidebar-refresh" @click="loadWorkspace">刷新</el-button></div>
      <el-scrollbar class="conversation-list">
        <div v-for="group in conversationGroups" :key="group.label" class="conversation-group">
          <div class="conversation-group-label">{{ group.label }}</div>
          <div v-for="item in group.items" :key="item.conversationId" class="conversation-item" :class="{ active: selectedConversation?.conversationId === item.conversationId }" @click="selectConversation(item)">
            <div class="conversation-icon">{{ (item.title || '新').slice(0, 1) }}</div>
            <div class="conversation-content"><div class="conversation-title">{{ item.title || '未命名会话' }}</div><div class="conversation-agent"><i />{{ item.agentId || '未选择 Agent' }}</div></div>
            <el-button text class="conversation-more" @click.stop="deleteConversation(item)">×</el-button>
          </div>
        </div>
        <div v-if="!filteredConversations.length" class="empty-conversations"><div class="empty-orb">✦</div><strong>还没有对话</strong><span>新建一个对话开始吧</span></div>
      </el-scrollbar>
    </el-aside>

    <el-main class="main-panel">
      <header class="topbar">
        <div class="topbar-status"><span class="status-dot" :class="{ connected: statusText === '已连接' }" />{{ statusText }}<span class="status-caption">工作台</span></div>
        <div class="topbar-actions">
          <el-select v-model="selectedAgentId" placeholder="选择 Agent" @change="handleAgentChange">
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
            <el-form label-position="top"><el-form-item label="Engine 地址"><el-input v-model="engineUrl" placeholder="http://localhost:5208" /></el-form-item><el-form-item label="Bearer Token（高级联调）"><el-input v-model="token" type="password" show-password placeholder="可选：从认证中心获取的 Access Token" /></el-form-item><el-form-item label="租户 ID"><el-input v-model="tenantId" placeholder="由认证中心 Token 中的 tid 决定" /></el-form-item></el-form>
            <el-descriptions :column="2" border class="identity-status"><el-descriptions-item label="当前用户">{{ currentUser?.userId || '未连接' }}</el-descriptions-item><el-descriptions-item label="当前租户">{{ currentUser?.tenantId || tenantId || '未识别' }}</el-descriptions-item><el-descriptions-item label="认证状态">{{ currentUser?.isAuthenticated ? '已认证' : '未认证' }}</el-descriptions-item><el-descriptions-item label="Token 状态">{{ token ? '已配置（Bearer）' : '未配置' }}</el-descriptions-item></el-descriptions>
            <el-alert title="Engine 的 Authentication:Mode 只由后端启动配置决定；这里仅选择登录方式并获取 Token。" type="info" :closable="false" />
            <section class="login-card"><div class="login-card-heading"><div><span class="eyebrow">IDENTITY PROVIDER</span><h4>登录 Engine</h4></div><span class="login-config-status">{{ authConfig ? '已读取后端登录配置' : '等待连接后读取' }}</span></div><el-radio-group v-model="loginMethod" class="login-methods"><el-radio-button value="password" :disabled="!authConfig?.password.enabled">账号密码</el-radio-button><el-radio-button value="microsoft" :disabled="!authConfig?.microsoft.enabled">Microsoft</el-radio-button></el-radio-group><template v-if="loginMethod === 'password'"><el-form label-position="top" class="login-form"><el-form-item label="账号"><el-input v-model="username" autocomplete="username" placeholder="name@example.com" /></el-form-item><el-form-item label="密码"><el-input v-model="password" type="password" show-password autocomplete="current-password" placeholder="请输入密码" /></el-form-item></el-form><el-button type="primary" :loading="authLoading" :disabled="!authConfig?.password.enabled" @click="loginWithPassword">账号密码登录</el-button><small v-if="!authConfig?.password.enabled" class="login-hint">请在 Engine Authentication:Login:Password 中配置 TokenEndpoint 后启用。</small></template><template v-else><p class="login-hint">将跳转到 Microsoft 登录页，使用 Authorization Code + PKCE 返回 Token。</p><el-button type="primary" :loading="authLoading" :disabled="!authConfig?.microsoft.enabled" @click="startMicrosoftLogin">使用 Microsoft 登录</el-button><small v-if="!authConfig?.microsoft.enabled" class="login-hint">请在 Engine Authentication:Login:Microsoft 中配置 Authority 和 ClientId 后启用。</small></template></section>
            <div class="button-row"><el-button type="primary" @click="connect">保存并连接</el-button><el-button @click="api.health('/health').then(() => ElMessage.success('Live 健康检查通过')).catch(notifyError)">测试 Live</el-button><el-button @click="api.health('/ready').then(() => ElMessage.success('Ready 健康检查通过')).catch(notifyError)">测试 Ready</el-button></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Agent 配置" name="agent">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">AGENT RUNTIME</span><h3>Agent 配置</h3><p>Agent 以卡片方式管理，点击编辑后在独立窗口配置模型与运行参数。</p></div><div class="section-actions"><el-button type="primary" plain @click="createAgent">新增 Agent</el-button><el-button @click="loadWorkspace">重新加载</el-button></div></div>
            <div class="agent-card-grid"><article v-for="agent in agents" :key="agent.agentId" class="agent-card"><div class="agent-card-top"><span class="resource-avatar agent-avatar">{{ (agent.name || agent.agentId).slice(0, 1) }}</span><span class="resource-status" /></div><h4>{{ agent.name || agent.agentId }}</h4><p>{{ agent.agentId }}</p><div class="agent-card-meta"><span>{{ agent.apiFormat || '未配置模型' }}</span><span>v{{ agent.currentVersion || 'draft' }}</span></div><el-button type="primary" plain @click="editAgent(agent.agentId)">编辑配置</el-button></article><button class="agent-card agent-card-add" @click="createAgent"><span>＋</span><strong>新增 Agent</strong><small>创建独立运行配置</small></button><div v-if="!agents.length" class="resource-empty">还没有 Agent</div></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="MCP 绑定" name="mcp">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>MCP 绑定</h3><p>以表格查看每条 MCP 的传输方式、地址和测试状态，连接由 Engine 服务端执行。</p></div><div class="section-actions"><el-button type="primary" plain @click="newMcp">新增 MCP</el-button></div></div>
            <el-table :data="mcpServers" class="capability-table" empty-text="还没有绑定 MCP"><el-table-column label="名称" min-width="170"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="传输" width="110"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址 / 命令" min-width="250" show-overflow-tooltip><template #default="scope">{{ scope.row.url || [scope.row.command, ...(scope.row.arguments || [])].filter(Boolean).join(' ') || '未配置' }}</template></el-table-column><el-table-column label="状态" width="100"><template #default="scope"><span class="table-status"><i />已绑定</span></template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="selectMcp(scope.$index); showMcpEditor = true">编辑</el-button><el-button link @click="testMcpRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectMcp(scope.$index); deleteMcp()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Skill 绑定" name="skill">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>Skill 绑定</h3><p>以表格查看每条 Skill 的 ID、状态和说明，支持逐条编辑与测试。</p></div><div class="section-actions"><el-button type="primary" plain @click="newSkill">新增 Skill</el-button></div></div>
            <el-table :data="skillDraft.instances" class="capability-table" empty-text="还没有绑定 Skill"><el-table-column label="Skill" min-width="180"><template #default="scope"><strong>{{ scope.row.name || '未命名 Skill' }}</strong><small class="table-subtext">{{ scope.row.skillId || '未设置 ID' }}</small></template></el-table-column><el-table-column label="来源 / 类型" width="150"><template #default="scope">{{ scope.row.source || 'Local' }}<span v-if="scope.row.type"> · {{ scope.row.type }}</span></template></el-table-column><el-table-column label="状态" width="110"><template #default="scope"><span class="table-status" :class="{ muted: !scope.row.enabled }"><i />{{ scope.row.enabled ? '已启用' : '已停用' }}</span></template></el-table-column><el-table-column label="说明" min-width="250" show-overflow-tooltip><template #default="scope">{{ scope.row.description || '—' }}</template></el-table-column><el-table-column label="操作" width="170" fixed="right"><template #default="scope"><el-button link type="primary" @click="editSkill(scope.$index)">编辑</el-button><el-button link @click="testSkillRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectSkill(scope.$index); removeSkill()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="RAG 绑定" name="rag">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>RAG 绑定</h3><p>按表格维护检索实例，并可逐条测试 RAG 服务地址。</p></div><div class="section-actions"><el-button type="primary" plain @click="newRag">新增 RAG</el-button></div></div>
            <div class="capability-summary"><span>当前 Agent：{{ selectedAgent?.name || selectedAgentId || '未选择' }}</span><strong>{{ ragEnabledText }}</strong></div><el-table :data="ragInstances" class="capability-table" empty-text="还没有绑定 RAG"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name || scope.row.id }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="类型" width="130"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="Endpoint" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.apiEndpoint || '未配置' }}</template></el-table-column><el-table-column label="状态" width="110"><template #default="scope"><span class="table-status" :class="{ muted: !scope.row.enabled }"><i />{{ scope.row.enabled ? '已启用' : '已停用' }}</span></template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="editRag(scope.$index)">编辑</el-button><el-button link @click="testRagRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectRag(scope.$index); deleteRag()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
      </el-tabs>
    </div>
  </el-dialog>

  <el-dialog v-model="showAgentEditor" class="editor-dialog" width="min(720px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <template #header><div class="editor-dialog-header"><div><span class="eyebrow">AGENT RUNTIME</span><h3>{{ config?.name || 'Agent 配置' }}</h3></div><el-tag effect="plain" round>{{ config?.agentId }}</el-tag></div></template>
    <el-form v-if="config" label-position="top"><el-form-item label="名称"><el-input v-model="config.name" /></el-form-item><el-form-item label="最大轮次"><el-input-number v-model="config.config.maxTurns" :min="1" :max="1000" /></el-form-item><el-form-item label="LLM 配置 JSON"><el-input v-model="llmJson" type="textarea" :rows="9" /></el-form-item></el-form><template #footer><el-button @click="showAgentEditor = false">取消</el-button><el-button type="primary" :loading="savingConfig" @click="saveConfig">保存 Agent 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpEditor" class="editor-dialog" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="名称"><el-input v-model="mcpDraft.name" placeholder="例如 local-tools" /></el-form-item><el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="HTTP" value="Http" /><el-option label="SSE" value="SSE" /><el-option label="Stdio（服务端执行）" value="Stdio" /></el-select></el-form-item><el-form-item v-if="mcpDraft.type !== 'Stdio'" label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com" /></el-form-item><template v-else><el-form-item label="Command"><el-input v-model="mcpDraft.command" placeholder="例如 node" /></el-form-item><el-form-item label="Arguments（每行一个）"><el-input v-model="mcpArgumentsText" type="textarea" :rows="3" /></el-form-item><el-form-item label="Working Directory"><el-input v-model="mcpDraft.workingDirectory" /></el-form-item></template><el-alert v-if="mcpResult" :title="`测试结果：${mcpResult.success ? '连接成功' : '连接失败'} · 权限${mcpResult.authorized ? '通过' : '拒绝'}`" :description="mcpResult.error || `延迟 ${mcpResult.latencyMs}ms，发现 ${mcpResult.toolCount} 个工具`" :type="mcpResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showMcpEditor = false">取消</el-button><el-button :loading="testingMcp" @click="testMcp">测试连接与权限</el-button><el-button type="primary" :disabled="!mcpDraft.name" @click="saveMcp">保存 MCP 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showSkillEditor" class="editor-dialog" title="编辑 Skill" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form v-if="selectedSkill" label-position="top"><el-form-item label="Skill ID"><el-input v-model="selectedSkill.skillId" placeholder="例如 weather" /></el-form-item><el-form-item label="名称"><el-input v-model="selectedSkill.name" placeholder="例如 天气查询" /></el-form-item><el-form-item label="说明"><el-input v-model="selectedSkill.description" type="textarea" :rows="3" /></el-form-item><el-form-item label="纳入 Agent 能力"><el-input v-model="enabledSkillText" placeholder="多个 Skill ID 用逗号分隔" /></el-form-item><el-form-item label="状态"><el-switch v-model="selectedSkill.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="skillResult" :title="skillResult.success ? 'Skill 配置测试通过' : 'Skill 配置测试失败'" :description="`已启用 ${skillResult.enabledCount || 0} 条，实例 ${skillResult.instanceCount || 0} 条`" :type="skillResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showSkillEditor = false">取消</el-button><el-button :loading="testingSkill" @click="testSkills">测试 Skill 配置</el-button><el-button type="primary" @click="saveSkills">保存 Skill 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showRagEditor" class="editor-dialog" title="编辑 RAG" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="RAG ID"><el-input v-model="ragDraft.id" placeholder="例如 knowledge-base" /></el-form-item><el-form-item label="名称"><el-input v-model="ragDraft.name" placeholder="例如 企业知识库" /></el-form-item><el-form-item label="类型"><el-select v-model="ragDraft.type"><el-option label="RAGFlow" value="ragflow" /><el-option label="Qdrant" value="qdrant" /></el-select></el-form-item><el-form-item label="Endpoint"><el-input v-model="ragDraft.apiEndpoint" placeholder="https://rag.example.com/api/search" /></el-form-item><el-form-item label="Collection / Dataset"><el-input v-model="ragDraft.collectionName" /></el-form-item><el-form-item label="API Key"><el-input v-model="ragDraft.apiKey" type="password" show-password placeholder="留空则保留已保存的密钥" /></el-form-item><el-form-item label="状态"><el-switch v-model="ragDraft.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="ragResult" :title="`测试结果：${ragResult.success ? '连接成功' : '连接失败'}`" :description="ragResult.error || `HTTP ${ragResult.statusCode || '-'} · 延迟 ${ragResult.latencyMs}ms`" :type="ragResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showRagEditor = false">取消</el-button><el-button :loading="testingRag" @click="testRag">测试连接</el-button><el-button type="primary" :disabled="!ragDraft.id" @click="saveRag">保存 RAG 配置</el-button></template>
  </el-dialog>
</template>

<style>
.message-attachments { display: flex; flex-wrap: wrap; gap: 5px; margin-bottom: 7px; }
.message-attachments span { display: inline-flex; align-items: center; padding: 4px 7px; color: #5d54e8; background: #fff; border: 1px solid #dfe2ff; border-radius: 6px; font-size: 11px; }
</style>
