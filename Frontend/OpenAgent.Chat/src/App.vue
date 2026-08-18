<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getAccessToken, getConnectionMode, getEngineBaseUrl, getRouterBaseUrl, getTenantId, makeLocalConversation, setAccessToken, setConnectionMode, setEngineBaseUrl, setRouterBaseUrl, setTenantId } from './api'
import ChatHeader from './components/ChatHeader.vue'
import ChatMessages from './components/ChatMessages.vue'
import ChatSidebar from './components/ChatSidebar.vue'
import MessageComposer from './components/MessageComposer.vue'
import HealthCheckPanel from './components/HealthCheckPanel.vue'
import { AUTO_AGENT_ID, type AgentConfigEntity, type AgentSummary, type AuthConfig, type ConnectionMode, type ConversationMessage, type ConversationRecord, type CurrentUserContext, type LlmProviderProfile, type LlmTestResult, type McpRuntimeStatus, type McpServerConfig, type McpTestResult, type MessageFile, type PendingFile, type RagConfig, type RagInstanceConfig, type RagTestResult, type SkillCatalogItem, type SkillInstanceConfig, type SkillSandboxStatus, type SkillsConfig } from './types'
import { usePanelLayout } from './composables/usePanelLayout'

const connectionMode = ref<ConnectionMode>(getConnectionMode())
const routerUrl = ref(getRouterBaseUrl())
const engineUrl = ref(getEngineBaseUrl())
const token = ref(getAccessToken())
const tenantId = ref(getTenantId())
const showSettings = ref(!(connectionMode.value === 'router' ? routerUrl.value : engineUrl.value))
const activeSettings = ref<'gateway' | 'health' | 'llm' | 'mcp' | 'skill' | 'agent' | 'rag'>('gateway')
const agents = ref<AgentSummary[]>([])
const currentUser = ref<CurrentUserContext | null>(null)
const conversations = ref<ConversationRecord[]>([])
const selectedConversation = ref<ConversationRecord | null>(null)
const selectedAgentId = ref(AUTO_AGENT_ID)
const message = ref('')
const search = ref('')
const loading = ref(false)
/** 当前流式请求的取消句柄，用于发送按钮在生成中切换为“停止”。 */
let streamAbort: AbortController | null = null
const loadingConversation = ref(false)
const savingConfig = ref(false)
const refreshingAgents = ref(false)
const testingMcp = ref(false)
const uploadingSkill = ref(false)
const testingRag = ref(false)
const statusText = ref('未连接')
const config = ref<AgentConfigEntity | null>(null)
const authConfig = ref<AuthConfig | null>(null)
const username = ref('')
const password = ref('')
const authLoading = ref(false)
const showAgentEditor = ref(false)
const isNewAgent = ref(false)
const showMcpEditor = ref(false)
const showRagEditor = ref(false)
const mcpDraft = ref<McpServerConfig>({ name: '', url: '', type: 'Http', arguments: [], environmentVariables: {}, protocolVersion: null })
const mcpServers = ref<McpServerConfig[]>([])
const mcpRuntime = ref<McpRuntimeStatus | null>(null)
const agentMcpIds = ref<string[]>([])
const selectedMcpIndex = ref(-1)
const mcpResult = ref<McpTestResult | null>(null)
const skillPackageInput = ref<HTMLInputElement | null>(null)
const showSkillTextEditor = ref(false)
const skillMarkdownDraft = ref('---\nname: my-skill\ndescription: Describe what this Skill does\n---\n\n# Instructions\n\n')
const skillCatalog = ref<SkillCatalogItem[]>([])
const skillRuntime = ref<SkillSandboxStatus | null>(null)
const updatingSkillExecution = ref('')
const skillDraft = ref<SkillsConfig>({ enabledSkills: [], instances: [] })
const ragDraft = ref<RagInstanceConfig>({ id: '', name: '', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: '', apiKey: '' })
const ragInstances = ref<RagInstanceConfig[]>([])
const selectedRagIndex = ref(-1)
const ragResult = ref<RagTestResult | null>(null)
const llmProfiles = ref<LlmProviderProfile[]>([])
const llmDraft = ref<LlmProviderProfile>(createDefaultLlm())
const selectedLlmIndex = ref(-1)
const llmResult = ref<LlmTestResult | null>(null)
const testingLlm = ref(false)
const savingLlm = ref(false)
const showLlmEditor = ref(false)
const isNewLlm = ref(false)
const pendingFiles = ref<PendingFile[]>([])
const themeMode = ref<'light' | 'dark'>(localStorage.getItem('openagent.ui.theme') === 'dark' ? 'dark' : 'light')
const { sidebarCollapsed, contextCollapsed, toggleSidebar, toggleContext, startSidebarResize, startContextResize } = usePanelLayout()
const currentMessages = computed(() => selectedConversation.value?.messages || [])
const enabledSkillIds = computed(() => new Set(skillDraft.value.enabledSkills))
const enabledRagIds = computed(() => new Set(config.value?.config.rag?.enabledRagInstanceIds || ragInstances.value.filter(item => item.enabled).map(item => item.id)))
const mcpArgumentsText = computed({
  get: () => (mcpDraft.value.arguments || []).join('\n'),
  set: (value: string) => { mcpDraft.value.arguments = value.split('\n').map(item => item.trim()).filter(Boolean) },
})
const mcpEnvironmentText = computed({
  get: () => Object.entries(mcpDraft.value.environmentVariables || {}).map(([key, value]) => `${key}=${value}`).join('\n'),
  set: (value: string) => {
    mcpDraft.value.environmentVariables = Object.fromEntries(value.split('\n').map(item => item.trim()).filter(Boolean).map(item => {
      const separator = item.indexOf('=')
      return separator < 1 ? [item, ''] : [item.slice(0, separator), item.slice(separator + 1)]
    }))
  },
})

function syncCapabilityDraftsToAgent(): void {
  if (!config.value) return
  config.value.config.mcp = {
    enabledServerIds: [...agentMcpIds.value],
    servers: [],
  }
  const catalogIds = new Set(skillCatalog.value.map(item => item.skillId.toLowerCase()))
  config.value.config.skills = {
    enabledSkills: [...skillDraft.value.enabledSkills],
    // Keep only legacy inline instances; catalog Skills are bound by ID.
    instances: skillDraft.value.instances
      .filter(item => !catalogIds.has(item.skillId.toLowerCase()))
      .map(item => ({ ...item })),
  }
}

const ragEnabledText = computed(() => config.value?.config.rag?.enabled ? '已启用' : '未启用')
const selectedAgent = computed(() => agents.value.find(agent => agent.agentId === selectedAgentId.value) || null)
const activeEndpointUrl = computed(() => connectionMode.value === 'router' ? routerUrl.value : engineUrl.value)
const activeEndpointLabel = computed(() => connectionMode.value === 'router' ? 'Router' : 'Engine')
const activeEndpointHost = computed(() => {
  try { return new URL(activeEndpointUrl.value).host }
  catch { return activeEndpointUrl.value || '未配置' }
})
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

function applyTheme(): void {
  document.documentElement.dataset.theme = themeMode.value
  // Element Plus 暗色主题依赖 html.dark class + dark css-vars。
  document.documentElement.classList.toggle('dark', themeMode.value === 'dark')
  localStorage.setItem('openagent.ui.theme', themeMode.value)
}

function toggleTheme(): void {
  themeMode.value = themeMode.value === 'dark' ? 'light' : 'dark'
  applyTheme()
}

function isSkillEnabled(skillId: string): boolean {
  return enabledSkillIds.value.has(skillId)
}

function toggleSkillBinding(skill: SkillInstanceConfig, enabled: boolean): void {
  const ids = new Set(skillDraft.value.enabledSkills)
  if (enabled) ids.add(skill.skillId)
  else ids.delete(skill.skillId)
  skill.enabled = enabled
  skillDraft.value.enabledSkills = Array.from(ids).filter(Boolean)
}

function toggleMcpBinding(server: McpServerConfig, enabled: boolean): void {
  const ids = new Set(agentMcpIds.value)
  if (enabled) ids.add(server.name)
  else ids.delete(server.name)
  agentMcpIds.value = [...ids]
}

function isRagEnabled(id: string): boolean {
  return enabledRagIds.value.has(id)
}

function toggleRagBinding(instance: RagInstanceConfig, enabled: boolean): void {
  const current = config.value?.config.rag || { enabled: false, enabledRagInstanceIds: [], instances: [] }
  const ids = new Set(current.enabledRagInstanceIds)
  if (enabled) ids.add(instance.id)
  else ids.delete(instance.id)
  instance.enabled = enabled
  if (config.value) config.value.config.rag = { ...current, enabled: ids.size > 0, enabledRagInstanceIds: Array.from(ids), instances: ragInstances.value.map(item => ({ ...item })) }
}

function notifyError(error: unknown): void {
  ElMessage.error(error instanceof Error ? error.message : '请求失败')
}

function createDefaultLlm(): LlmProviderProfile {
  return {
    id: '',
    name: '',
    format: 'OpenAIChatCompletions',
    endpoint: 'https://api.openai.com/v1',
    apiKey: '',
    temperature: 0.7,
  }
}

async function connect(): Promise<void> {
  setConnectionMode(connectionMode.value)
  setRouterBaseUrl(routerUrl.value)
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
  } catch {
    authConfig.value = null
  }
}

async function loginWithPassword(): Promise<void> {
  if (!username.value.trim() || !password.value) return notifyError(new Error('请输入账号和密码'))
  authLoading.value = true
  try {
    const result = await api.passwordLogin(username.value.trim(), password.value)
    setAccessToken(result.access_token, result.token_type || 'Basic')
    token.value = result.access_token
    password.value = ''
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
    if ((connectionMode.value === 'engine' && selectedAgentId.value === AUTO_AGENT_ID)
      || (selectedAgentId.value !== AUTO_AGENT_ID && !agents.value.some(item => item.agentId === selectedAgentId.value))) {
      selectedAgentId.value = agents.value[0]?.agentId || ''
      config.value = null
    }
  } catch (error) {
    notifyError(error)
  } finally {
    loading.value = false
  }
}

async function loadLlmProfiles(): Promise<void> {
  try {
    llmProfiles.value = await api.listLlmProfiles()
  } catch (error) {
    notifyError(error)
  }
}

async function loadMcpProfiles(): Promise<void> {
  try {
    const [profiles, runtime] = await Promise.all([api.listMcpProfiles(), api.getMcpRuntime()])
    mcpServers.value = profiles
    mcpRuntime.value = runtime
  } catch (error) {
    notifyError(error)
  }
}

async function loadSkillCatalog(): Promise<void> {
  try {
    const [skills, runtime] = await Promise.all([api.listSkills(), api.getSkillRuntime()])
    skillCatalog.value = skills
    skillRuntime.value = runtime
  } catch (error) {
    notifyError(error)
  }
}

function selectLlm(index: number): void {
  const profile = llmProfiles.value[index]
  if (!profile) return
  selectedLlmIndex.value = index
  llmDraft.value = { ...profile }
  llmResult.value = null
}

function newLlm(): void {
  selectedLlmIndex.value = -1
  llmDraft.value = createDefaultLlm()
  llmResult.value = null
  isNewLlm.value = true
  showLlmEditor.value = true
}

function editLlm(index: number): void {
  selectLlm(index)
  isNewLlm.value = false
  showLlmEditor.value = true
}

async function deleteLlm(): Promise<void> {
  const profile = llmProfiles.value[selectedLlmIndex.value]
  if (!profile) return
  try {
    await ElMessageBox.confirm(`确认删除大模型配置「${profile.name}」吗？删除后绑定它的 Agent 将无法执行。`, '删除大模型配置', { type: 'warning' })
    await api.deleteLlmProfile(profile.id)
    llmProfiles.value.splice(selectedLlmIndex.value, 1)
    selectedLlmIndex.value = llmProfiles.value.length ? 0 : -1
    if (selectedLlmIndex.value >= 0) selectLlm(selectedLlmIndex.value)
    ElMessage.success('大模型配置已删除')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function saveLlm(): Promise<void> {
  const profile = llmDraft.value
  const id = profile.id.trim()
  if (!id || !/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(id)) return notifyError(new Error('LLM ID 只能使用字母、数字、点、下划线或短横线'))
  if (!profile.name.trim() || !profile.endpoint.trim()) return notifyError(new Error('请填写名称和 Endpoint'))
  profile.id = id
  savingLlm.value = true
  try {
    const saved = await api.saveLlmProfile(id, profile)
    const existingIndex = llmProfiles.value.findIndex(item => item.id === saved.id)
    if (existingIndex >= 0) llmProfiles.value[existingIndex] = saved
    else llmProfiles.value.push(saved)
    selectLlm(existingIndex >= 0 ? existingIndex : llmProfiles.value.length - 1)
    showLlmEditor.value = false
    ElMessage.success('大模型配置已保存')
  } catch (error) {
    notifyError(error)
  } finally {
    savingLlm.value = false
  }
}

async function testLlm(): Promise<void> {
  testingLlm.value = true
  try {
    llmResult.value = await api.testLlmProfile(llmDraft.value)
  } catch (error) {
    notifyError(error)
  } finally {
    testingLlm.value = false
  }
}


function applyLlmProfile(providerId: string): void {
  const profile = llmProfiles.value.find(item => item.id === providerId)
  if (!profile || !config.value) return
  config.value.config.llm.format = profile.format
  config.value.config.llm.endpoint = profile.endpoint
  config.value.config.llm.temperature = profile.temperature
}

async function refreshAgents(): Promise<void> {
  refreshingAgents.value = true
  try {
    const refreshed = await api.listAgents()
    agents.value = refreshed
    if ((connectionMode.value === 'engine' && selectedAgentId.value === AUTO_AGENT_ID)
      || (selectedAgentId.value !== AUTO_AGENT_ID && !refreshed.some(item => item.agentId === selectedAgentId.value))) {
      selectedAgentId.value = refreshed[0]?.agentId || ''
      config.value = null
    }
    if (selectedAgentId.value && selectedAgentId.value !== AUTO_AGENT_ID && activeSettings.value === 'agent') {
      await loadConfig()
    }
    ElMessage.success('Agent 列表已刷新')
  } catch (error) {
    notifyError(error)
  } finally {
    refreshingAgents.value = false
  }
}

async function refreshConversations(): Promise<void> {
  try {
    const refreshed = await api.listConversations()
    const current = selectedConversation.value
    conversations.value = current && !refreshed.some(item => item.conversationId === current.conversationId)
      ? [current, ...refreshed]
      : refreshed
  } catch (error) {
    notifyError(error)
  }
}

async function selectConversation(item: ConversationRecord): Promise<void> {
  selectedConversation.value = item
  selectedAgentId.value = item.agentId || selectedAgentId.value
  if (item.messages?.length) {
    await hydrateFilePreviews(item)
    return
  }
  loadingConversation.value = true
  try {
    const detail = await api.getConversation(item.conversationId)
    await hydrateFilePreviews(detail)
    selectedConversation.value = detail
  } catch (error) {
    notifyError(error)
  } finally {
    loadingConversation.value = false
  }
}

async function hydrateFilePreviews(conversation: ConversationRecord): Promise<void> {
  const files = conversation.messages?.flatMap(item => item.files || []) || []
  await Promise.all(files.map(async file => {
    if (!file.fileId || file.previewUrl || file.previewText) return
    try {
      if (file.mediaType.startsWith('image/')) {
        file.previewUrl = await api.loadFilePreview(file.fileId, conversation.conversationId)
      } else if (isTextPreview(file.mediaType)) {
        file.previewText = await api.readFileText(file.fileId, conversation.conversationId)
      }
    } catch {
      // The file remains downloadable even when preview loading fails.
    }
  }))
}

function isTextPreview(mediaType: string): boolean {
  return mediaType.startsWith('text/') || mediaType === 'application/json'
}

async function downloadFile(file: MessageFile): Promise<void> {
  if (!file.fileId || !selectedConversation.value) return
  try {
    await api.downloadFile(file.fileId, file.fileName, selectedConversation.value.conversationId)
  } catch (error) {
    notifyError(error)
  }
}

function newConversation(): void {
  selectedConversation.value = null
  message.value = ''
  pendingFiles.value = []
}

function handleAgentChange(): void {
  // 切换 Agent 时保留当前会话与输入内容：实际场景中 Agent 可随时切换。
  config.value = null
  agentMcpIds.value = []
  mcpServers.value = []
  selectedMcpIndex.value = -1
  skillDraft.value = { enabledSkills: [], instances: [] }
  ragInstances.value = []
  selectedRagIndex.value = -1
}

function llmName(agent: AgentSummary): string {
  if (agent.llmProvider) {
    const profile = llmProfiles.value.find(item => item.id === agent.llmProvider)
    return profile?.name || agent.llmProvider
  }
  return agent.llmModel || '未配置模型'
}

function llmId(agent: AgentSummary): string {
  return agent.llmModel || ''
}

function selectAgent(agentId: string): void {
  if (selectedAgentId.value === agentId) return
  selectedAgentId.value = agentId
  handleAgentChange()
  void loadConfig()
}

function handleFilesChange(files: PendingFile[]): void {
  const existing = new Map(pendingFiles.value.map(item => [item.id, item]))
  pendingFiles.value = files.map(item => existing.get(item.id) || item)
  for (const item of pendingFiles.value.filter(item => item.state === 'uploading' && !existing.has(item.id))) {
    void uploadPendingFile(item.id)
  }
}

function retryPendingFile(id: string): void {
  const file = pendingFiles.value.find(item => item.id === id)
  if (!file || file.state === 'uploading') return
  file.state = 'uploading'
  file.error = undefined
  void uploadPendingFile(id)
}

async function uploadPendingFile(id: string): Promise<void> {
  const pending = pendingFiles.value.find(item => item.id === id)
  if (!pending) return
  try {
    const asset = await api.uploadFile(pending.file)
    const current = pendingFiles.value.find(item => item.id === id)
    if (!current) return
    current.asset = asset
    current.state = 'ready'
  } catch (error) {
    const current = pendingFiles.value.find(item => item.id === id)
    if (!current) return
    current.state = 'failed'
    current.error = error instanceof Error ? error.message : '上传失败'
    notifyError(error)
  }
}

async function toMessageFile(item: PendingFile): Promise<MessageFile> {
  const asset = item.asset
  if (!asset) throw new Error('文件尚未上传完成')
  return {
    fileId: asset.fileId,
    fileName: asset.fileName,
    mediaType: asset.mediaType,
    length: asset.length,
    previewUrl: asset.mediaType.startsWith('image/') ? URL.createObjectURL(item.file) : undefined,
    previewText: isTextPreview(asset.mediaType) ? await item.file.text() : undefined,
  }
}

function stopStreaming(): void {
  streamAbort?.abort()
}

async function send(): Promise<void> {
  const content = message.value.trim()
  const hasFiles = pendingFiles.value.length > 0
  if ((!content && !hasFiles) || !selectedAgentId.value || loading.value) return
  if (pendingFiles.value.some(item => item.state !== 'ready' || !item.asset)) {
    notifyError(new Error('请等待文件上传完成，或移除上传失败的文件后再发送'))
    return
  }
  const requestContent = content || '请处理我上传的文件'
  const isNewConversation = !selectedConversation.value
  const uploaded = pendingFiles.value
    .map(item => item.asset)
    .filter((asset): asset is NonNullable<typeof asset> => asset != null)
  const requestedAgentId = selectedAgentId.value === AUTO_AGENT_ID
    ? selectedConversation.value?.agentId
    : selectedAgentId.value
  const local = selectedConversation.value || makeLocalConversation(requestedAgentId || '', requestContent)
  if (isNewConversation) {
    local.messages = []
    local.messageCount = 0
    selectedConversation.value = local
    conversations.value = [local, ...conversations.value.filter(item => item.conversationId !== local.conversationId)]
  }
  const conversation = selectedConversation.value
  if (!conversation) return
  const conversationId = conversation.conversationId
  // 首次对话不携带任何 conversationId：由引擎生成并在 done 事件回传，前端记录后用于后续消息。
  // 这样 router 对首次消息会执行意图识别，而不是当成续聊直接转发。
  const sendConversationId = isNewConversation ? undefined : conversationId
  loading.value = true
  const controller = new AbortController()
  streamAbort = controller
  let streamError: { title?: string; detail?: string; traceId?: string } | undefined
  let flushStream: (() => void) | undefined
  let returnedConversationId: string | undefined
  try {
    conversation.messages ||= []
    const messageFiles = await Promise.all(pendingFiles.value.map(toMessageFile))
    conversation.messages.push({
      messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
      role: 'user', content: content || '已上传文件', timestamp: new Date().toISOString(),
      files: messageFiles,
    })
    conversation.messageCount = conversation.messages.length
    conversation.updatedAt = new Date().toISOString()
    conversation.lastMessageAt = conversation.updatedAt
    message.value = ''
    pendingFiles.value = []
    let assistant = ''
    let reasoning = ''
    let lastFlush = 0
    conversation.messages.push({
      messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
      role: 'assistant', content: '', timestamp: new Date().toISOString(),
    })
    const assistantMessage = conversation.messages[conversation.messages.length - 1]
    flushStream = (): void => {
      assistantMessage.content = assistant
      assistantMessage.reasoning = reasoning || undefined
      lastFlush = performance.now()
    }
    // 流式写入节流：思考/正文很长时按帧批量刷新，避免每个事件都触发整段重渲染导致卡顿。
    for await (const event of api.streamChat(
      requestContent,
      requestedAgentId,
      sendConversationId,
      uploaded.map(asset => asset.fileId),
      sendConversationId,
      controller.signal,
    )) {
      if (event.type === 'content') {
        assistant += event.content || ''
        if (performance.now() - lastFlush > 100) flushStream?.()
      } else if (event.type === 'reasoning') {
        reasoning += event.content || ''
        if (performance.now() - lastFlush > 100) flushStream?.()
      } else if (event.type === 'tool_call') {
        assistantMessage.toolActivities ||= []
        assistantMessage.toolActivities.push({
          name: event.toolName || '工具',
          callId: event.toolCallId,
          arguments: event.toolArguments,
        })
      } else if (event.type === 'done' && event.status) {
        conversation.status = event.status as ConversationRecord['status']
      } else if (event.type === 'error') {
        // 捕获流式错误，交给 catch 以独立错误卡片展示；不混入助手内容。
        streamError = {
          title: event.error?.title,
          detail: event.error?.detail || 'Agent 执行失败',
          traceId: event.error?.traceId,
        }
        throw new Error(streamError.detail)
      }
      if (event.conversationId) returnedConversationId = event.conversationId
    }
    flushStream?.()
    const persisted = await api.getConversation(returnedConversationId || conversationId)
    await hydrateFilePreviews(persisted)
    selectedConversation.value = persisted
    await refreshConversations()
    // 初次会话：若后端意图识别将对话路由到了其他 Agent，更新右上角选择器并提示。
    if (isNewConversation && persisted.agentId && persisted.agentId !== requestedAgentId) {
      const routed = agents.value.find(agent => agent.agentId === persisted.agentId)
      selectedAgentId.value = persisted.agentId
      ElMessage.info(`已由意图识别路由到 Agent「${routed?.name || persisted.agentId}」`)
    }
  } catch (error) {
    if (controller.signal.aborted) {
      // 用户主动停止：保留已生成的部分内容并正常存档，不当作错误处理。
      conversation.status = 'Cancelled'
      conversation.updatedAt = new Date().toISOString()
      conversation.lastMessageAt = conversation.updatedAt
      flushStream?.()
      // 引擎在流开始时就下发了真实 conversationId；用它替换本地临时 ID，
      // 保证下次输入继续同一会话，之前的消息与文件才能被引擎识别。
      const persistedConversationId = returnedConversationId || conversationId
      if (returnedConversationId) {
        conversation.conversationId = returnedConversationId
      }
      try {
        const persisted = await api.getConversation(persistedConversationId)
        await hydrateFilePreviews(persisted)
        // 仅当服务端会话已有消息时才替换本地视图；否则保留本地部分内容，
        // 避免中止后页面退回空的“新会话”欢迎页。
        if (persisted.messages?.length) {
          selectedConversation.value = persisted
        }
      } catch {
        // 首次会话未持久化时保留本地部分内容作为存档。
      }
      await refreshConversations().catch(() => undefined)
    } else {
      conversation.status = 'Failed'
      const lastMessage = conversation.messages?.at(-1)
      if (lastMessage?.role === 'assistant') {
        // 错误信息不写入历史，以独立错误卡片就近展示；保留已流式的部分内容。
        lastMessage.error = {
          title: streamError?.title || 'Agent 执行失败',
          detail: streamError?.detail || (error instanceof Error ? error.message : '执行失败'),
          traceId: streamError?.traceId,
        }
      }
      notifyError(error)
    }
  } finally {
    streamAbort = null
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

async function loadConfig(agentId = selectedAgentId.value): Promise<void> {
  if (!agentId || agentId === AUTO_AGENT_ID) return
  try {
    const loadedConfig = await api.getAgentConfig(agentId)
    const catalog = await api.listSkills().catch(() => [] as SkillCatalogItem[])
    config.value = loadedConfig
    skillCatalog.value = catalog
    agentMcpIds.value = [...(config.value.config.mcp?.enabledServerIds || [])]
    for (const legacy of config.value.config.mcp?.servers || []) {
      if (!agentMcpIds.value.includes(legacy.name)) agentMcpIds.value.push(legacy.name)
      if (!mcpServers.value.some(item => item.name.toLowerCase() === legacy.name.toLowerCase())) mcpServers.value.push(legacy)
    }
    skillDraft.value = {
      enabledSkills: [...config.value.config.skills.enabledSkills],
      instances: [...skillCatalog.value, ...config.value.config.skills.instances.filter(item => !skillCatalog.value.some(catalog => catalog.skillId.toLowerCase() === item.skillId.toLowerCase()))]
        .map(item => ({ ...item, enabled: config.value?.config.skills.enabledSkills.includes(item.skillId) ?? item.enabled })),
    }
    const enabledRagInstanceIds = new Set(config.value.config.rag?.enabledRagInstanceIds || [])
    ragInstances.value = (config.value.config.rag?.instances || []).map(item => ({ ...item, enabled: enabledRagInstanceIds.size ? enabledRagInstanceIds.has(item.id) : item.enabled }))
    selectedRagIndex.value = ragInstances.value.length ? 0 : -1
    if (selectedRagIndex.value >= 0) selectRag(selectedRagIndex.value)
  } catch (error) {
    notifyError(error)
  }
}

function createDefaultMcp(): McpServerConfig {
  return { name: '', url: '', type: 'Http', arguments: [], environmentVariables: {}, protocolVersion: null }
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

async function removeMcp(index: number): Promise<void> {
  const current = mcpServers.value[index]
  if (!current) return
  try {
    await ElMessageBox.confirm(`确认移除 MCP「${current.name}」吗？`, '移除 MCP', { type: 'warning' })
    await api.deleteMcpProfile(current.name)
    mcpServers.value.splice(index, 1)
    selectedMcpIndex.value = mcpServers.value.length ? Math.min(index, mcpServers.value.length - 1) : -1
    if (selectedMcpIndex.value >= 0) selectMcp(selectedMcpIndex.value)
    agentMcpIds.value = agentMcpIds.value.filter(id => id.toLowerCase() !== current.name.toLowerCase())
    ElMessage.success('MCP 配置已删除；已绑定的 Agent 需要重新选择配置')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function editAgent(agentId: string): Promise<void> {
  selectedAgentId.value = agentId
  handleAgentChange()
  await Promise.all([loadConfig(agentId), loadLlmProfiles(), loadMcpProfiles()])
  if (config.value?.config.llm.provider) applyLlmProfile(config.value.config.llm.provider)
  isNewAgent.value = false
  showAgentEditor.value = true
}

function chooseSkillPackage(): void {
  skillPackageInput.value?.click()
}

function openSkillTextEditor(): void {
  skillMarkdownDraft.value = '---\nname: my-skill\ndescription: Describe what this Skill does\n---\n\n# Instructions\n\n'
  showSkillTextEditor.value = true
}

function parseSkillFrontmatter(markdown: string): { name: string; description: string } | null {
  const lines = markdown.replace(/^\uFEFF/, '').split(/\r?\n/)
  if (lines[0]?.trim() !== '---') return null
  const end = lines.findIndex((line, index) => index > 0 && line.trim() === '---')
  if (end < 0) return null
  const values = new Map<string, string>()
  for (const line of lines.slice(1, end)) {
    const separator = line.indexOf(':')
    if (separator > 0) values.set(line.slice(0, separator).trim().toLowerCase(), line.slice(separator + 1).trim().replace(/^['"]|['"]$/g, ''))
  }
  const name = values.get('name')?.trim() || ''
  const description = values.get('description')?.trim() || ''
  return name && description ? { name, description } : null
}

async function uploadSkillFile(file: File): Promise<void> {
  const extension = file.name.toLowerCase().split('.').pop()
  if (extension !== 'zip' && extension !== 'md') throw new Error('Skill 只能上传 .zip 或单文件 .md')
  if (file.size === 0 || file.size > 4 * 1024 * 1024) throw new Error('Skill 文件必须在 1B 到 4MB 之间')
  const installed = await api.uploadSkillCatalog(file)
  skillCatalog.value = [installed.skill, ...skillCatalog.value.filter(item => item.skillId.toLowerCase() !== installed.skill.skillId.toLowerCase())]
  ElMessage.success('Skill 已校验并写入 OSS 解压目录；请在 Agent 中选择绑定')
}

async function uploadSkillPackage(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  uploadingSkill.value = true
  try {
    await uploadSkillFile(file)
  } catch (error) {
    notifyError(error)
  } finally {
    uploadingSkill.value = false
  }
}

async function deleteSkillCatalog(skill: SkillCatalogItem): Promise<void> {
  try {
    await ElMessageBox.confirm(`确认删除 Skill「${skill.name}」吗？删除后所有 Agent 的该绑定都会失效。`, '删除 Skill', { type: 'warning' })
    await api.deleteSkillCatalog(skill.skillId)
    skillCatalog.value = skillCatalog.value.filter(item => item.skillId.toLowerCase() !== skill.skillId.toLowerCase())
    skillDraft.value.enabledSkills = skillDraft.value.enabledSkills.filter(id => id.toLowerCase() !== skill.skillId.toLowerCase())
    ElMessage.success('Skill 已从目录删除')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') notifyError(error)
  }
}

async function setSkillScriptExecution(skill: SkillCatalogItem, enabled: boolean): Promise<void> {
  updatingSkillExecution.value = skill.skillId
  try {
    const updated = await api.setSkillScriptExecution(skill.skillId, enabled)
    skillCatalog.value = skillCatalog.value.map(item => item.skillId === skill.skillId ? updated : item)
    ElMessage.success(enabled ? 'Skill 脚本执行已启用' : 'Skill 脚本执行已停用')
  } catch (error) {
    notifyError(error)
  } finally {
    updatingSkillExecution.value = ''
  }
}

async function saveTextSkill(): Promise<void> {
  const frontmatter = parseSkillFrontmatter(skillMarkdownDraft.value)
  if (!frontmatter) {
    notifyError(new Error('Skill Markdown 必须以 YAML frontmatter 开始，并包含 name 与 description'))
    return
  }
  uploadingSkill.value = true
  try {
    await uploadSkillFile(new File([skillMarkdownDraft.value], `${frontmatter.name}.md`, { type: 'text/markdown' }))
    showSkillTextEditor.value = false
  } catch (error) {
    notifyError(error)
  } finally {
    uploadingSkill.value = false
  }
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
    description: '',
    status: 0,
    currentVersion: '',
    config: {
      instructions: '',
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
  const agentId = `agent-${crypto.randomUUID().slice(0, 8)}`
  selectedAgentId.value = agentId
  handleAgentChange()
  config.value = createDefaultAgent(agentId, '')
  isNewAgent.value = true
  mcpServers.value = []
  skillDraft.value = { enabledSkills: [], instances: [] }
  ragInstances.value = []
  await Promise.all([loadLlmProfiles(), loadMcpProfiles(), loadSkillCatalog()])
  showAgentEditor.value = true
}

async function saveConfig(): Promise<void> {
  if (!config.value) return
  const agentId = config.value.agentId.trim()
  if (!agentId || !/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(agentId)) {
    notifyError(new Error('Agent ID 只能使用字母、数字、点、下划线或短横线'))
    return
  }
  if (!config.value.name.trim()) {
    notifyError(new Error('请输入 Agent 名称'))
    return
  }
  config.value.agentId = agentId
  syncCapabilityDraftsToAgent()
  config.value.config.rag = {
    ...(config.value.config.rag || { enabled: false, enabledRagInstanceIds: [], instances: [] }),
    enabledRagInstanceIds: [...(config.value.config.rag?.enabledRagInstanceIds || [])],
    instances: ragInstances.value.map(item => ({ ...item })),
  }
  savingConfig.value = true
  try {
    const saved = await api.saveAgentConfig(agentId, config.value)
    config.value = saved
    selectedAgentId.value = agentId
    agents.value = [
      ...agents.value.filter(item => item.agentId !== agentId),
      { agentId, name: saved.name, description: saved.description, status: saved.status, currentVersion: saved.currentVersion, apiFormat: String(saved.config.llm.format || ''), llmProvider: saved.config.llm.provider, llmModel: saved.config.llm.modelId },
    ]
    isNewAgent.value = false
    showAgentEditor.value = false
    ElMessage.success('Agent 配置已保存')
  } catch (error) {
    notifyError(error)
  } finally {
    savingConfig.value = false
  }
}

async function saveMcp(): Promise<void> {
  const name = mcpDraft.value.name.trim()
  if (!name) {
    notifyError(new Error('请输入 MCP 名称'))
    return
  }
  const duplicate = mcpServers.value.findIndex((item, index) =>
    index !== selectedMcpIndex.value && item.name.trim().toLowerCase() === name.toLowerCase())
  if (duplicate >= 0) {
    notifyError(new Error(`MCP「${name}」已经存在`))
    return
  }

  const saved: McpServerConfig = {
    ...mcpDraft.value,
    name,
    arguments: [...(mcpDraft.value.arguments || [])],
    environmentVariables: { ...(mcpDraft.value.environmentVariables || {}) },
  }
  try {
    const persisted = await api.saveMcpProfile(name, saved)
    if (selectedMcpIndex.value >= 0 && selectedMcpIndex.value < mcpServers.value.length) mcpServers.value[selectedMcpIndex.value] = persisted
    else {
      mcpServers.value.push(persisted)
      selectedMcpIndex.value = mcpServers.value.length - 1
    }
    selectMcp(selectedMcpIndex.value)
    showMcpEditor.value = false
    ElMessage.success('MCP 配置已保存，可在 Agent 中选择绑定')
  } catch (error) {
    notifyError(error)
  }
}

async function testMcp(): Promise<void> {
  testingMcp.value = true
  try {
    mcpResult.value = await api.testMcp(mcpDraft.value, config.value?.agentId)
  } catch (error) {
    notifyError(error)
  } finally {
    testingMcp.value = false
  }
}

function openSettings(panel: typeof activeSettings.value): void {
  activeSettings.value = panel
  showSettings.value = true
  if (panel === 'llm') void loadLlmProfiles()
  if (panel === 'mcp') void loadMcpProfiles()
  if (panel === 'skill') void loadSkillCatalog()
  if (panel === 'agent') {
    void loadConfig()
    void loadLlmProfiles()
    void loadMcpProfiles()
  }
  if (panel === 'rag') void loadConfig()
}

function handleSettingsTabChange(name: string | number): void {
  if (name === 'llm') void loadLlmProfiles()
  if (name === 'mcp') void loadMcpProfiles()
  if (name === 'skill') void loadSkillCatalog()
  if (name === 'agent') {
    void loadConfig()
    void loadLlmProfiles()
    void loadMcpProfiles()
  }
  if (name === 'rag') void loadConfig()
}

onMounted(() => {
  applyTheme()
  if (activeEndpointUrl.value) {
    void connect()
  }
})
</script>

<template>
  <el-container class="app-shell" :class="{ 'sidebar-collapsed': sidebarCollapsed }">
    <ChatSidebar :conversations="conversations" :selected-conversation-id="selectedConversation?.conversationId" :search="search" :loading="loading" :status-text="statusText" :current-user="currentUser" @update:search="search = $event" @new="newConversation" @settings="openSettings('gateway')" @refresh="loadWorkspace" @select="selectConversation" @delete="deleteConversation" @toggle-collapse="toggleSidebar" @resize-start="startSidebarResize" />
    <button v-if="sidebarCollapsed" class="panel-restore sidebar-restore" type="button" aria-label="展开侧栏" title="展开侧栏" @click="toggleSidebar">›</button>

    <el-main class="main-panel">
      <ChatHeader :status-text="statusText" :agents="agents" :selected-agent-id="selectedAgentId" :allow-auto="connectionMode === 'router'" :refreshing-agents="refreshingAgents" :title="selectedConversation?.title || '新对话'" :theme-mode="themeMode" @update:selected-agent-id="selectedAgentId = $event" @agent-change="handleAgentChange" @refresh-agents="refreshAgents" @settings="openSettings('gateway')" @toggle-theme="toggleTheme" />

      <div class="workspace-grid" :class="{ 'context-collapsed': contextCollapsed }">
        <section class="chat-card">
          <ChatMessages :messages="currentMessages" :loading="loadingConversation" :current-user="currentUser" :streaming="loading" @suggest="message = $event" @download="downloadFile" />
          <MessageComposer :model-value="message" :endpoint-url="activeEndpointUrl" :endpoint-label="activeEndpointLabel" :selected-agent-id="selectedAgentId" :loading="loading" :pending-files="pendingFiles" @update:model-value="message = $event" @files-change="handleFilesChange" @retry-file="retryPendingFile" @send="send" @stop="stopStreaming" />
        </section>
        <aside class="context-panel">
          <div class="context-panel-head"><span class="context-label">INSPECTOR</span><button class="panel-collapse-btn" type="button" aria-label="收起上下文面板" title="收起" @click="toggleContext">›</button></div>
          <section><span class="context-label">ROUTING</span><strong>{{ routeMode }}</strong><p>{{ connectionMode === 'router' && selectedAgentId === AUTO_AGENT_ID ? '由意图识别 Agent 分析请求并选择目标。' : (selectedAgent?.description || selectedAgentId) }}</p><dl><div><dt>Agent</dt><dd>{{ connectionMode === 'router' && selectedAgentId === AUTO_AGENT_ID ? '由模型选择' : (selectedAgent?.name || selectedAgentId) }}</dd></div><div><dt>协议</dt><dd>{{ selectedAgent?.apiFormat || (connectionMode === 'router' ? '自动' : '—') }}</dd></div></dl></section>
          <section><span class="context-label">IDENTITY</span><dl><div><dt>用户</dt><dd>{{ currentUser?.userId || 'Guest' }}</dd></div><div><dt>租户</dt><dd>{{ currentUser?.tenantId || tenantId || '—' }}</dd></div><div><dt>{{ activeEndpointLabel }}</dt><dd :title="activeEndpointUrl">{{ activeEndpointHost }}</dd></div></dl></section>
          <section><span class="context-label">CONVERSATION</span><dl><div><dt>消息</dt><dd>{{ currentMessages.length }}</dd></div><div><dt>状态</dt><dd>{{ selectedConversation?.status ?? '新建' }}</dd></div><div><dt>ID</dt><dd class="truncate" :title="selectedConversation?.conversationId">{{ selectedConversation?.conversationId || '尚未创建' }}</dd></div></dl></section>
          <el-button class="diagnostics-shortcut" @click="openSettings('health')">运行平台健康检查</el-button>
          <div class="context-resize" @pointerdown="startContextResize" />
        </aside>
      </div>
      <button v-if="contextCollapsed" class="panel-restore context-restore" type="button" aria-label="展开上下文面板" title="展开上下文面板" @click="toggleContext">‹</button>
    </el-main>
  </el-container>

  <el-dialog v-model="showSettings" class="settings-dialog" modal-class="settings-overlay" width="min(1180px, calc(100vw - 40px))" top="3vh" :close-on-click-modal="false" destroy-on-close>
    <template #header>
      <div class="settings-header"><div><span class="eyebrow">OPENAGENT CONTROL PLANE</span><h2>工作台设置</h2></div><span class="settings-endpoint">{{ activeEndpointLabel }} · {{ activeEndpointHost }}</span></div>
    </template>
    <div class="settings-body">
      <el-tabs v-model="activeSettings" tab-position="left" class="settings-tabs" @tab-change="handleSettingsTabChange">
        <el-tab-pane label="连接" name="gateway">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CONNECTION</span><h3>服务连接与身份</h3><p>Router 模式提供意图路由、外部 Agent 和 Engine 服务发现；Engine 模式用于直接联调单个 Engine。</p></div><span class="connection-badge" :class="{ online: statusText === '已连接' }"><i />{{ statusText }}</span></div>
            <el-form label-position="top" class="connection-form"><el-form-item label="连接模式"><el-radio-group v-model="connectionMode"><el-radio-button value="router">Router</el-radio-button><el-radio-button value="engine">直连 Engine</el-radio-button></el-radio-group></el-form-item><el-form-item label="Router 地址"><el-input v-model="routerUrl" placeholder="http://localhost:5001" /></el-form-item><el-form-item label="Engine 地址"><el-input v-model="engineUrl" placeholder="http://localhost:5000" /></el-form-item><el-form-item label="租户 ID"><el-input v-model="tenantId" placeholder="可选：用于当前工作台隔离" /></el-form-item></el-form>
            <el-descriptions :column="2" border class="identity-status"><el-descriptions-item label="当前用户">{{ currentUser?.userId || '未连接' }}</el-descriptions-item><el-descriptions-item label="当前租户">{{ currentUser?.tenantId || tenantId || '未识别' }}</el-descriptions-item><el-descriptions-item label="认证状态">{{ currentUser?.isAuthenticated ? '已认证' : '未认证' }}</el-descriptions-item><el-descriptions-item label="登录状态">{{ token ? '已登录（Basic）' : '未登录' }}</el-descriptions-item></el-descriptions>
            <el-alert title="当前 Basic 模式用于开发联调；生产环境应在 Gateway 接入企业身份提供方并启用统一权限策略。" type="info" :closable="false" />
            <section class="login-card"><div class="login-card-heading"><div><span class="eyebrow">BASIC ACCOUNT</span><h4>登录 {{ activeEndpointLabel }}</h4></div><span class="login-config-status">{{ authConfig?.mode || 'Basic' }}</span></div><el-form label-position="top" class="login-form"><el-form-item label="账号"><el-input v-model="username" autocomplete="username" placeholder="请输入账号" /></el-form-item><el-form-item label="密码"><el-input v-model="password" type="password" show-password autocomplete="current-password" placeholder="请输入密码" /></el-form-item></el-form><el-button type="primary" :loading="authLoading" :disabled="!authConfig?.password.enabled" @click="loginWithPassword">账号密码登录</el-button><small class="login-hint">凭据只保存在当前浏览器会话，不写入本地持久化存储。</small></section>
            <div class="button-row"><el-button type="primary" @click="connect">保存并连接</el-button><el-button @click="api.health('/health').then(() => ElMessage.success('Live 健康检查通过')).catch(notifyError)">测试 Live</el-button><el-button @click="api.health('/ready').then(() => ElMessage.success('Ready 健康检查通过')).catch(notifyError)">测试 Ready</el-button></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="健康检查" name="health">
          <section class="settings-section">
            <HealthCheckPanel />
          </section>
        </el-tab-pane>
        <el-tab-pane label="LLM 配置" name="llm">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MODEL PROVIDERS</span><h3>大模型供应商</h3><p>这里维护协议、Endpoint 和密钥；具体模型 ID 属于 Agent 配置，选择供应商后只在 Agent 中填写。</p></div><div class="section-actions"><el-button @click="loadLlmProfiles">刷新</el-button><el-button type="primary" plain @click="newLlm">新增供应商</el-button></div></div>
            <el-table :data="llmProfiles" class="capability-table" empty-text="还没有大模型供应商"><el-table-column label="名称" min-width="140"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="协议" width="160"><template #default="scope"><el-tag size="small" round>{{ scope.row.format }}</el-tag></template></el-table-column><el-table-column label="模型归属" min-width="120"><template #default>由 Agent 指定</template></el-table-column><el-table-column label="Endpoint" min-width="200" show-overflow-tooltip><template #default="scope">{{ scope.row.endpoint }}</template></el-table-column><el-table-column label="API Key" min-width="120"><template #default="scope">{{ scope.row.apiKey ? '••••••••' : '未配置' }}</template></el-table-column><el-table-column label="操作" width="160" fixed="right"><template #default="scope"><el-button link type="primary" @click="editLlm(scope.$index)">编辑</el-button><el-button link @click="selectLlm(scope.$index); testLlm(); showLlmEditor = true">测试</el-button><el-button link type="danger" @click="selectLlm(scope.$index); deleteLlm()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="MCP 配置" name="mcp">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MCP CATALOG</span><h3>MCP 配置</h3><p>独立维护 MCP Server；Agent 页面只选择已注册的 Server，不在 Agent 中复制 Endpoint、命令和密钥。</p></div><div class="section-actions"><el-button @click="loadMcpProfiles">刷新</el-button><el-button type="primary" plain @click="newMcp">新增 MCP</el-button></div></div>
            <el-alert v-if="mcpRuntime" :type="mcpRuntime.stdioEnabled ? 'warning' : 'info'" :closable="false" show-icon><template #title>Stdio {{ mcpRuntime.stdioEnabled ? '已启用（可信宿主进程）' : '已停用' }}</template><p v-if="mcpRuntime.stdioEnabled">仅允许命令：{{ mcpRuntime.allowedCommands.join('、') || '未配置' }}。Stdio 会在 Engine 宿主启动进程，不等同于脚本隔离沙盒。</p><p v-else>生产配置默认禁止启动本地 MCP 进程；HTTP / SSE MCP 不受此开关影响。</p></el-alert>
            <el-table :data="mcpServers" class="capability-table" empty-text="还没有 MCP 配置"><el-table-column label="名称" min-width="160"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址/命令" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.url || scope.row.command || '未配置' }}</template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="selectMcp(scope.$index); showMcpEditor = true">编辑</el-button><el-button link @click="selectMcp(scope.$index); testMcp(); showMcpEditor = true">测试</el-button><el-button link type="danger" @click="removeMcp(scope.$index)">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Skill 配置" name="skill">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">SKILL CATALOG</span><h3>Skill 配置</h3><p>独立维护官方 Skill 目录；Agent 页面只选择 Skill ID，绑定关系保存到 Agent 配置。</p></div><div class="section-actions"><input ref="skillPackageInput" type="file" hidden accept=".zip,.md" @change="uploadSkillPackage" /><el-button @click="loadSkillCatalog">刷新</el-button><el-button :loading="uploadingSkill" type="primary" plain @click="chooseSkillPackage">上传 ZIP / MD</el-button><el-button type="primary" plain @click="openSkillTextEditor">手动填写</el-button></div></div>
            <el-alert v-if="skillRuntime" :type="skillRuntime.enabled ? 'success' : 'info'" :closable="false" show-icon><template #title>本地脚本沙盒{{ skillRuntime.enabled ? '可用' : '未启用' }}</template><p v-if="skillRuntime.enabled">隔离方式：{{ skillRuntime.isolation }}；支持 {{ skillRuntime.supportedExtensions.join('、') }}；单次超时 {{ skillRuntime.timeoutSeconds }} 秒。每个 Skill 仍需单独授权。</p><p v-else>Skill 指令和资源仍可使用；脚本工具不会暴露给模型。启用脚本前必须配置独立隔离服务。</p></el-alert>
            <el-table :data="skillCatalog" class="capability-table" empty-text="还没有 Skill"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.skillId }}</small></template></el-table-column><el-table-column label="说明" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.description }}</template></el-table-column><el-table-column label="内容" width="120"><template #default="scope">{{ scope.row.resourceCount || 0 }} 资源 / {{ scope.row.scriptCount || 0 }} 脚本</template></el-table-column><el-table-column label="脚本执行" width="140"><template #default="scope"><el-switch :model-value="scope.row.allowScriptExecution" :loading="updatingSkillExecution === scope.row.skillId" :disabled="!skillRuntime?.enabled || !scope.row.scriptCount" inline-prompt active-text="允许" inactive-text="禁止" @change="setSkillScriptExecution(scope.row, Boolean($event))" /></template></el-table-column><el-table-column label="来源" width="150"><template #default="scope">{{ scope.row.packageFileName }}</template></el-table-column><el-table-column label="操作" width="100" fixed="right"><template #default="scope"><el-button link type="danger" @click="deleteSkillCatalog(scope.row)">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Agent 配置" name="agent">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">AGENT RUNTIME</span><h3>Agent 配置</h3><p>Agent 以卡片方式管理，点击编辑后在独立窗口配置模型与运行参数。</p></div><div class="section-actions"><el-button @click="refreshAgents" :loading="refreshingAgents">刷新 Agent</el-button><el-button type="primary" plain @click="createAgent">新增 Agent</el-button></div></div>
            <div class="agent-card-grid"><article v-for="agent in agents" :key="agent.agentId" class="agent-card"><h4>{{ agent.name || agent.agentId }}</h4><p>{{ agent.description || agent.agentId }}</p><div class="agent-card-meta"><span>{{ llmName(agent) }}</span><span>{{ llmId(agent) }}</span></div><el-button type="primary" plain @click="editAgent(agent.agentId)">编辑配置</el-button></article><button class="agent-card agent-card-add" @click="createAgent"><span>＋</span><strong>新增 Agent</strong><small>创建独立运行配置</small></button><div v-if="!agents.length" class="resource-empty">还没有 Agent</div></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="RAG 绑定" name="rag">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>RAG 绑定</h3><p>按表格维护检索实例，并可逐条测试 RAG 服务地址。</p></div><div class="section-actions"><el-button type="primary" plain @click="newRag">新增 RAG</el-button></div></div>
            <div class="capability-summary"><strong>{{ ragEnabledText }}</strong></div><el-table :data="ragInstances" class="capability-table" empty-text="还没有绑定 RAG"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name || scope.row.id }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="类型" width="130"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="Endpoint" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.apiEndpoint || '未配置' }}</template></el-table-column><el-table-column label="状态" width="110"><template #default="scope"><span class="table-status" :class="{ muted: !isRagEnabled(scope.row.id) }"><i />{{ isRagEnabled(scope.row.id) ? '已绑定' : '未绑定' }}</span></template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="editRag(scope.$index)">编辑</el-button><el-button link @click="testRagRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectRag(scope.$index); deleteRag()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
      </el-tabs>
    </div>
  </el-dialog>

  <el-dialog v-model="showAgentEditor" class="editor-dialog agent-editor-dialog" modal-class="editor-overlay" width="min(920px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <template #header><div class="editor-dialog-header"><div><span class="eyebrow">AGENT RUNTIME</span><h3>{{ isNewAgent ? '创建 Agent' : (config?.name || 'Agent 配置') }}</h3></div><el-tag effect="plain" round>{{ config?.agentId }}</el-tag></div></template>
    <div v-if="config" class="agent-editor">
      <section class="agent-editor-section">
        <div class="agent-editor-section-heading"><div><span class="eyebrow">PROFILE</span><h4>基础信息</h4><p>先给 Agent 一个清晰的身份，再设置它的运行边界。</p></div><span class="editor-section-index">01</span></div>
        <el-form label-position="top" class="agent-form-grid">
          <el-form-item label="Agent ID"><el-input v-model="config.agentId" :disabled="!isNewAgent" placeholder="例如 customer-support" /><small class="form-help">只能使用字母、数字、点、下划线或短横线。</small></el-form-item>
          <el-form-item label="显示名称"><el-input v-model="config.name" placeholder="例如 客服助手" /></el-form-item>
          <el-form-item label="能力描述" class="span-two"><el-input v-model="config.description" type="textarea" :rows="2" placeholder="说明这个 Agent 擅长处理的请求，供意图识别 Agent 选择。" /></el-form-item>
          <el-form-item label="系统指令" class="span-two"><el-input v-model="config.config.instructions" type="textarea" :rows="4" placeholder="定义 Agent 的角色、边界和输出要求。意图识别 Agent 应要求只返回结构化选择结果。" /></el-form-item>
          <el-form-item label="最大连续轮次"><el-input-number v-model="config.config.maxTurns" :min="1" :max="1000" controls-position="right" /><small class="form-help">限制一次任务中的最大推理轮次。</small></el-form-item>
          <el-form-item label="发布状态"><div class="agent-readonly-value"><el-tag round effect="plain">{{ config.status === 2 ? 'Snapshot' : config.status === 1 ? 'Pending review' : 'Draft' }}</el-tag><span>版本 {{ config.currentVersion || '尚未发布' }}</span></div></el-form-item>
        </el-form>
      </section>

      <section class="agent-editor-section">
        <div class="agent-editor-section-heading"><div><span class="eyebrow">MODEL</span><h4>模型连接</h4><p>使用表单配置模型供应商、模型、协议和连接地址。</p></div><span class="editor-section-index">02</span></div>
        <el-form label-position="top" class="agent-form-grid">
          <el-form-item label="大模型供应商"><el-select v-model="config.config.llm.provider" class="full-width" filterable placeholder="选择已维护的供应商" @change="applyLlmProfile"><el-option v-for="profile in llmProfiles" :key="profile.id" :label="profile.name" :value="profile.id" /></el-select><small class="form-help">供应商的协议、Endpoint、API Key 在 LLM 配置页面维护。</small></el-form-item>
          <el-form-item label="模型 ID"><el-input v-model="config.config.llm.modelId" placeholder="例如 gpt-4o" /><small class="form-help">模型 ID 属于 Agent；选择供应商后这里只允许修改模型 ID。</small></el-form-item>
          <el-form-item label="API 格式"><el-select v-model="config.config.llm.format" class="full-width" :disabled="Boolean(config.config.llm.provider)"><el-option label="OpenAI Chat Completions" value="OpenAIChatCompletions" /><el-option label="OpenAI Responses" value="OpenAIResponses" /><el-option label="Anthropic Messages" value="AnthropicMessages" /></el-select></el-form-item>
          <el-form-item label="Temperature"><el-input-number v-model="config.config.llm.temperature" :min="0" :max="2" :step="0.1" :precision="1" controls-position="right" :disabled="Boolean(config.config.llm.provider)" /><small class="form-help">选择供应商后使用供应商配置。</small></el-form-item>
          <el-form-item label="Endpoint" class="span-two"><el-input v-model="config.config.llm.endpoint" placeholder="由供应商配置提供" :disabled="Boolean(config.config.llm.provider)" /></el-form-item>
          <el-form-item label="API Key" class="span-two"><el-input v-model="config.config.llm.apiKey" type="password" show-password placeholder="由供应商配置提供" :disabled="Boolean(config.config.llm.provider)" /></el-form-item>
        </el-form>
      </section>

      <section class="agent-editor-section">
        <div class="agent-editor-section-heading"><div><span class="eyebrow">CAPABILITY BINDINGS</span><h4>能力绑定</h4><p>当前 Agent 的 MCP、Skill、RAG 以卡片展示；勾选即可启用或停用 Skill 与 RAG。</p></div><span class="editor-section-index">03</span></div>
        <div class="binding-groups">
          <article class="binding-group"><div class="binding-group-heading"><div><strong>MCP</strong><small>从独立 MCP 配置中选择，绑定关系保存到 Agent 配置</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('mcp')">管理 MCP 配置</el-button></div><div v-if="mcpServers.length" class="binding-list"><label v-for="server in mcpServers" :key="server.name" class="binding-item binding-check-item"><span class="binding-icon mcp-avatar">M</span><div><strong>{{ server.name }}</strong><small>{{ server.type }} · {{ server.url || server.command || 'Stdio' }}</small></div><el-checkbox :model-value="agentMcpIds.includes(server.name)" @change="toggleMcpBinding(server, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 MCP 配置，请先在 MCP 配置页面新增。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>Skill</strong><small>从独立 Skill 目录中选择，绑定关系保存到 Agent 配置</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('skill')">管理 Skill 配置</el-button></div><div v-if="skillCatalog.length" class="binding-list"><label v-for="skill in skillCatalog" :key="skill.skillId" class="binding-item binding-check-item"><span class="binding-icon skill-avatar">S</span><div><strong>{{ skill.name || '未命名 Skill' }}</strong><small>{{ skill.skillId }} · {{ skill.packageFileName?.toLowerCase().endsWith('.md') ? '单文件 SKILL.md' : 'Skill 目录' }}</small></div><el-checkbox :model-value="isSkillEnabled(skill.skillId)" @change="toggleSkillBinding(skill, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 Skill 配置，请先在 Skill 配置页面新增。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>RAG</strong><small>知识检索数据源</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('rag')">管理 RAG</el-button></div><div v-if="ragInstances.length" class="binding-list"><label v-for="rag in ragInstances" :key="rag.id" class="binding-item binding-check-item"><span class="binding-icon rag-avatar">R</span><div><strong>{{ rag.name || rag.id }}</strong><small>{{ rag.type }} · {{ rag.collectionName || '默认数据集' }}</small></div><el-checkbox :model-value="isRagEnabled(rag.id)" @change="toggleRagBinding(rag, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 RAG，去 RAG 表格中新增。</div></article>
        </div>
      </section>
    </div>
    <template #footer><el-button @click="showAgentEditor = false">取消</el-button><el-button type="primary" :loading="savingConfig" @click="saveConfig">保存 Agent 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showLlmEditor" class="editor-dialog" modal-class="editor-overlay" :title="isNewLlm ? '新增大模型配置' : '编辑大模型配置'" width="min(720px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top" class="agent-form-grid"><el-form-item label="配置 ID"><el-input v-model="llmDraft.id" :disabled="!isNewLlm" placeholder="例如 openai-prod" /><small class="form-help">Agent 通过这个 ID 绑定供应商配置。</small></el-form-item><el-form-item label="显示名称"><el-input v-model="llmDraft.name" placeholder="例如 OpenAI 生产环境" /></el-form-item><el-form-item label="API 格式"><el-select v-model="llmDraft.format" class="full-width"><el-option label="OpenAI Chat Completions" value="OpenAIChatCompletions" /><el-option label="OpenAI Responses" value="OpenAIResponses" /><el-option label="Anthropic Messages" value="AnthropicMessages" /></el-select></el-form-item><el-form-item label="Temperature"><el-input-number v-model="llmDraft.temperature" :min="0" :max="2" :step="0.1" :precision="1" controls-position="right" /></el-form-item><el-form-item label="Endpoint"><el-input v-model="llmDraft.endpoint" placeholder="https://api.openai.com/v1" /></el-form-item><el-form-item label="API Key" class="span-two"><el-input v-model="llmDraft.apiKey" type="text" placeholder="请输入 API Key" /><small class="form-help">模型 ID 不在供应商配置中维护，由 Agent 选择供应商后填写。</small></el-form-item><el-alert v-if="llmResult" class="span-two" :title="`测试结果：${llmResult.success ? '连接和权限通过' : '连接失败'}${llmResult.statusCode ? ` · HTTP ${llmResult.statusCode}` : ''}`" :description="llmResult.error || `模型 ${llmResult.modelId || '由 Agent 指定'} · 延迟 ${llmResult.latencyMs}ms`" :type="llmResult.success ? 'success' : 'warning'" :closable="false" /></el-form>
    <template #footer><el-button @click="showLlmEditor = false">取消</el-button><el-button :loading="testingLlm" @click="testLlm">测试连接与权限</el-button><el-button type="primary" :loading="savingLlm" :disabled="!llmDraft.id" @click="saveLlm">保存大模型配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top">
      <el-form-item label="名称"><el-input v-model="mcpDraft.name" :disabled="selectedMcpIndex >= 0" placeholder="例如 local-tools" /><small class="form-help">名称同时作为 MCP 配置 ID；编辑时不可修改，避免已有 Agent 绑定失效。</small></el-form-item>
      <el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="Streamable HTTP" value="Http" /><el-option label="Legacy SSE" value="SSE" /><el-option label="Stdio（服务端执行）" value="Stdio" /></el-select></el-form-item>
      <el-form-item label="MCP 协议版本"><el-select v-model="mcpDraft.protocolVersion" clearable filterable allow-create placeholder="自动协商（推荐）"><el-option label="2026-07-28" value="2026-07-28" /><el-option label="2025-11-25" value="2025-11-25" /><el-option label="2025-06-18" value="2025-06-18" /><el-option label="2025-03-26" value="2025-03-26" /><el-option label="2024-11-05" value="2024-11-05" /></el-select><small class="form-help">留空由官方 SDK 自动协商；指定版本作为最低版本，服务器降级到更早版本时连接会失败。</small></el-form-item>
      <el-form-item v-if="mcpDraft.type !== 'Stdio'" label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com/mcp" /></el-form-item>
      <template v-else><el-form-item label="Command"><el-input v-model="mcpDraft.command" placeholder="例如 node" /></el-form-item><el-form-item label="Arguments（每行一个）"><el-input v-model="mcpArgumentsText" type="textarea" :rows="3" /></el-form-item><el-form-item label="Working Directory"><el-input v-model="mcpDraft.workingDirectory" /></el-form-item><el-form-item label="环境变量（KEY=VALUE，每行一个）"><el-input v-model="mcpEnvironmentText" type="textarea" :rows="3" /></el-form-item></template>
      <el-alert v-if="mcpResult" :title="`测试结果：${mcpResult.success ? '连接成功' : '连接失败'} · 权限${mcpResult.authorized ? '通过' : '拒绝'}`" :description="mcpResult.error || `协商版本 ${mcpResult.negotiatedProtocolVersion || '未知'} · ${mcpResult.latencyMs}ms · ${mcpResult.toolCount} 个工具`" :type="mcpResult.success ? 'success' : 'warning'" :closable="false" />
    </el-form><template #footer><el-button @click="showMcpEditor = false">取消</el-button><el-button :loading="testingMcp" @click="testMcp">测试连接、版本与权限</el-button><el-button type="primary" :disabled="!mcpDraft.name" @click="saveMcp">保存 MCP 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showSkillTextEditor" class="editor-dialog" modal-class="editor-overlay" title="手动填写 Markdown Skill" width="min(760px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-alert title="单文件 Skill 必须包含 YAML frontmatter 的 name、description，以及后续 Markdown 指令内容。保存后服务端会再次校验并按目录 Skill 存入 OSS。" type="info" :closable="false" />
    <el-input v-model="skillMarkdownDraft" class="skill-markdown-input" type="textarea" :rows="18" spellcheck="false" />
    <template #footer><el-button @click="showSkillTextEditor = false">取消</el-button><el-button type="primary" :loading="uploadingSkill" @click="saveTextSkill">校验并保存 Skill</el-button></template>
  </el-dialog>

  <el-dialog v-model="showRagEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 RAG" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="RAG ID"><el-input v-model="ragDraft.id" placeholder="例如 knowledge-base" /></el-form-item><el-form-item label="名称"><el-input v-model="ragDraft.name" placeholder="例如 企业知识库" /></el-form-item><el-form-item label="类型"><el-select v-model="ragDraft.type"><el-option label="RAGFlow" value="ragflow" /><el-option label="Qdrant" value="qdrant" /></el-select></el-form-item><el-form-item label="Endpoint"><el-input v-model="ragDraft.apiEndpoint" placeholder="https://rag.example.com/api/search" /></el-form-item><el-form-item label="Collection / Dataset"><el-input v-model="ragDraft.collectionName" /></el-form-item><el-form-item label="API Key"><el-input v-model="ragDraft.apiKey" type="password" show-password placeholder="留空则保留已保存的密钥" /></el-form-item><el-form-item label="状态"><el-switch v-model="ragDraft.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="ragResult" :title="`测试结果：${ragResult.success ? '连接成功' : '连接失败'}`" :description="ragResult.error || `HTTP ${ragResult.statusCode || '-'} · 延迟 ${ragResult.latencyMs}ms`" :type="ragResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showRagEditor = false">取消</el-button><el-button :loading="testingRag" @click="testRag">测试连接</el-button><el-button type="primary" :disabled="!ragDraft.id" @click="saveRag">保存 RAG 配置</el-button></template>
  </el-dialog>
</template>
