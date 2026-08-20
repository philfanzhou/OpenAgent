<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowReactive, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, ApiError, AUTH_FAILURE_EVENT, clearAuthentication, getAccessToken, getConnectionMode, getEngineBaseUrl, getRouterBaseUrl, getTenantId, makeLocalConversation, setAccessToken, setConnectionMode, setEngineBaseUrl, setRouterBaseUrl, setTenantId } from './api'
import { beginOidcLogin, cleanAuthorizationCallbackUrl, clearAuthState, completeOidcLogin, LOGIN_HASH, sanitizeReturnHash, WORKSPACE_HASH } from './auth'
import ChatHeader from './components/ChatHeader.vue'
import ChatMessages from './components/ChatMessages.vue'
import ChatSidebar from './components/ChatSidebar.vue'
import LoginPage from './components/LoginPage.vue'
import MessageComposer from './components/MessageComposer.vue'
import { formatTokenCount, summarizeConversationUsage } from './tokenUsage'
import HealthCheckPanel from './components/HealthCheckPanel.vue'
import { mergeAssistantSnapshot } from './messagePresentation'
import { AUTO_AGENT_ID, type AgentConfigEntity, type AgentSummary, type AuthConfig, type ConnectionMode, type ConversationMessage, type ConversationRecord, type CurrentUserContext, type LlmModelOption, type LlmModelSelection, type LlmProviderProfile, type LlmTestResult, type McpServerConfig, type McpTestResult, type MessageFile, type PendingFile, type RagConfig, type RagInstanceConfig, type RagTestResult, type SkillCatalogItem, type SkillInstanceConfig, type SkillsConfig } from './types'
import { usePanelLayout } from './composables/usePanelLayout'
import { useConversationStreams } from './composables/useConversationStreams'
import { mergeConversationRecords, replaceConversationRecord, selectionMatchesConversation } from './conversationCollection'
import { createStreamingAssistantContentState, enqueueAssistantContent, markAssistantPhaseBoundary } from './streamingAssistantContent'
import { createTypewriterQueue, type TypewriterQueue } from './typewriterQueue'

const selectedConversationStorageKey = 'openagent.chat.selected-conversation-id'

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
const selectedModelKey = ref('')
const modelScope = ref<'conversation' | 'message'>('conversation')
const conversationModelDirty = ref(false)
const message = ref('')
const search = ref('')
const workspaceLoading = ref(false)
const conversationDetailRequests = shallowReactive(new Map<string, string>())
const conversationStreams = useConversationStreams()
const savingConfig = ref(false)
const refreshingAgents = ref(false)
const testingMcp = ref(false)
const uploadingSkill = ref(false)
const testingRag = ref(false)
const statusText = ref('未连接')
const config = ref<AgentConfigEntity | null>(null)
const authConfig = ref<AuthConfig | null>(null)
const authLoading = ref(false)
const authView = ref<'restoring' | 'login' | 'workspace' | 'forbidden'>('restoring')
const authError = ref('')
const authReason = ref('')
const authReturnHash = ref(sanitizeReturnHash(window.location.hash))
const showAgentEditor = ref(false)
const isNewAgent = ref(false)
const showMcpEditor = ref(false)
const showRagEditor = ref(false)
const mcpDraft = ref<McpServerConfig>({ name: '', url: '', type: 'Http', protocolVersion: null })
const mcpServers = ref<McpServerConfig[]>([])
const agentMcpIds = ref<string[]>([])
const showMcpBindingPicker = ref(false)
const mcpBindingOptions = ref<McpServerConfig[]>([])
const loadingMcpBindingOptions = ref(false)
const selectedMcpIndex = ref(-1)
const mcpResult = ref<McpTestResult | null>(null)
const skillPackageInput = ref<HTMLInputElement | null>(null)
const showSkillTextEditor = ref(false)
const skillMarkdownDraft = ref('---\nname: my-skill\ndescription: Describe what this Skill does\n---\n\n# Instructions\n\n')
const skillEditorMode = ref<'form' | 'markdown'>('form')
const skillEditorName = ref('')
const skillEditorDescription = ref('')
const skillEditorInstructions = ref('')
const editingSkillId = ref('')
const showSkillBindingPicker = ref(false)
const skillBindingOptions = ref<SkillCatalogItem[]>([])
const loadingSkillBindingOptions = ref(false)
const skillCatalog = ref<SkillCatalogItem[]>([])
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
const selectedConversationStreaming = computed(() => conversationStreams.isStreaming(selectedConversation.value?.conversationId))
const streamingConversationIds = computed(() => conversationStreams.activeConversationIds())
const loadingConversation = computed(() => selectedConversation.value
  ? conversationDetailRequests.has(selectedConversation.value.conversationId)
  : false)
const conversationStatusText = computed(() => {
  if (!selectedConversation.value) return '新建'
  if (selectedConversation.value.status === 'Running' && !selectedConversationStreaming.value) {
    return 'Running（当前页面未连接流）'
  }
  return selectedConversation.value.status
})
const currentUsageSummary = computed(() => summarizeConversationUsage(currentMessages.value))
const enabledSkillIds = computed(() => new Set(skillDraft.value.enabledSkills))
const enabledRagIds = computed(() => new Set(config.value?.config.rag?.enabledRagInstanceIds || ragInstances.value.filter(item => item.enabled).map(item => item.id)))
const boundMcpServers = computed(() => agentMcpIds.value.map(id =>
  mcpServers.value.find(item => item.name.toLowerCase() === id.toLowerCase())
  || mcpBindingOptions.value.find(item => item.name.toLowerCase() === id.toLowerCase())
  || { name: id, url: '', type: 'Http' as const }))
const boundSkills = computed(() => skillDraft.value.enabledSkills.map(id =>
  skillCatalog.value.find(item => item.skillId.toLowerCase() === id.toLowerCase())
  || skillBindingOptions.value.find(item => item.skillId.toLowerCase() === id.toLowerCase())
  || skillDraft.value.instances.find(item => item.skillId.toLowerCase() === id.toLowerCase())
  || { skillId: id, name: id, enabled: true }))

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
const availableModels = computed<LlmModelOption[]>(() => selectedAgent.value?.availableModels || [])
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
  if (enabled && !mcpServers.value.some(item => item.name.toLowerCase() === server.name.toLowerCase())) {
    mcpServers.value = [...mcpServers.value, server]
  }
}

async function openMcpBindingPicker(): Promise<void> {
  loadingMcpBindingOptions.value = true
  try {
    mcpBindingOptions.value = await api.listMcpProfiles()
    showMcpBindingPicker.value = true
  } catch (error) {
    notifyError(error)
  } finally {
    loadingMcpBindingOptions.value = false
  }
}

async function openSkillBindingPicker(): Promise<void> {
  loadingSkillBindingOptions.value = true
  try {
    skillBindingOptions.value = await api.listSkills()
    showSkillBindingPicker.value = true
  } catch (error) {
    notifyError(error)
  } finally {
    loadingSkillBindingOptions.value = false
  }
}

function removeMcpBinding(name: string): void {
  agentMcpIds.value = agentMcpIds.value.filter(id => id.toLowerCase() !== name.toLowerCase())
}

function removeSkillBinding(skillId: string): void {
  skillDraft.value.enabledSkills = skillDraft.value.enabledSkills.filter(id => id.toLowerCase() !== skillId.toLowerCase())
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
    modelIds: [],
    isEnabled: true,
    endpoint: 'https://api.openai.com/v1',
    apiKey: '',
    temperature: 0.7,
  }
}

async function connect(): Promise<void> {
  setConnectionMode(connectionMode.value)
  setRouterBaseUrl(routerUrl.value)
  setEngineBaseUrl(engineUrl.value)
  setTenantId(tenantId.value)
  if (token.value && !getAccessToken()) {
    clearAuthState()
    showLogin('服务地址已变更，请针对新的认证边界重新登录。')
    await detectAuthentication(false)
    return
  }
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
  authConfig.value = await api.getAuthConfig()
}

function updateLoginConnection(value: {
  mode: ConnectionMode
  routerUrl: string
  engineUrl: string
  tenantId: string
}): void {
  connectionMode.value = value.mode
  routerUrl.value = value.routerUrl
  engineUrl.value = value.engineUrl
  tenantId.value = value.tenantId
  setConnectionMode(value.mode)
  setRouterBaseUrl(value.routerUrl)
  setEngineBaseUrl(value.engineUrl)
  setTenantId(value.tenantId)
  authConfig.value = null
  authError.value = ''
  void detectAuthentication()
}

function resetWorkspace(): void {
  void Promise.allSettled(conversationStreams.cancelAll('logout'))
  currentUser.value = null
  agents.value = []
  conversations.value = []
  selectedConversation.value = null
  conversationDetailRequests.clear()
  pendingFiles.value = []
  message.value = ''
  config.value = null
  llmDraft.value = createDefaultLlm()
  llmProfiles.value = []
  mcpDraft.value = { name: '', url: '', type: 'Http', protocolVersion: null }
  mcpServers.value = []
  skillDraft.value = { enabledSkills: [], instances: [] }
  ragDraft.value = { id: '', name: '', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: '', apiKey: '' }
  ragInstances.value = []
  llmResult.value = null
  mcpResult.value = null
  ragResult.value = null
  showAgentEditor.value = false
  showLlmEditor.value = false
  showMcpEditor.value = false
  showRagEditor.value = false
  token.value = ''
  showSettings.value = false
  statusText.value = '未连接'
}

function showLogin(reason = ''): void {
  if (window.location.hash && window.location.hash !== LOGIN_HASH && window.location.hash !== '#/forbidden') {
    authReturnHash.value = sanitizeReturnHash(window.location.hash)
  }
  resetWorkspace()
  authView.value = 'login'
  authReason.value = reason
  window.history.replaceState(null, document.title, `${window.location.pathname}${LOGIN_HASH}`)
}

async function establishSession(returnHash = WORKSPACE_HASH): Promise<void> {
  const user = await api.getCurrentUser()
  if (!user.isAuthenticated) throw new Error('服务端未建立已认证身份')
  currentUser.value = user
  if (user.tenantId) {
    tenantId.value = user.tenantId
    setTenantId(user.tenantId)
  }
  token.value = getAccessToken()
  authError.value = ''
  authReason.value = ''
  authView.value = 'workspace'
  statusText.value = '已连接'
  window.history.replaceState(null, document.title, `${window.location.pathname}${sanitizeReturnHash(returnHash)}`)
  await loadWorkspace()
}

async function loginWithPassword(credentials: { username: string; password: string }): Promise<void> {
  authLoading.value = true
  authError.value = ''
  try {
    if (!authConfig.value?.development || authConfig.value.mode !== 'Basic' || !authConfig.value.password.enabled) {
      throw new Error('Development Basic 登录未启用')
    }
    const result = await api.passwordLogin(credentials.username, credentials.password)
    setAccessToken(result.access_token, result.token_type || 'Basic', result.expires_in)
    token.value = result.access_token
    await establishSession(authReturnHash.value)
  } catch {
    clearAuthentication()
    token.value = ''
    authError.value = '登录失败，请检查连接、账号和租户后重试。'
  } finally {
    authLoading.value = false
  }
}

async function loginWithOidc(): Promise<void> {
  if (!authConfig.value) return
  authLoading.value = true
  authError.value = ''
  try {
    await beginOidcLogin(authConfig.value, authReturnHash.value)
  } catch {
    authError.value = '无法启动企业登录，请检查身份提供方配置。'
    authLoading.value = false
  }
}

function returnToWorkspace(): void {
  authView.value = 'workspace'
  window.history.replaceState(null, document.title, `${window.location.pathname}${WORKSPACE_HASH}`)
}

function handleAuthenticationFailure(event: Event): void {
  const status = (event as CustomEvent<{ status: number }>).detail?.status
  if (status === 401) {
    clearAuthState()
    showLogin('登录已过期，请重新登录。')
  } else if (status === 403 && authView.value === 'workspace') {
    authView.value = 'forbidden'
    window.history.replaceState(null, document.title, `${window.location.pathname}#/forbidden`)
  }
}

async function detectAuthentication(restoreSession = true): Promise<void> {
  authLoading.value = true
  authError.value = ''
  try {
    await loadAuthConfig()
    if (restoreSession && getAccessToken()) {
      authView.value = 'restoring'
      await establishSession(authReturnHash.value)
      return
    }
    showLogin(authReason.value)
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) return
    authConfig.value = null
    authView.value = 'login'
    authError.value = '无法读取服务认证配置，请检查连接地址后重试。'
  } finally {
    authLoading.value = false
  }
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

async function logout(): Promise<void> {
  await Promise.allSettled(conversationStreams.cancelAll('logout'))
  clearAuthState()
  authConfig.value = null
  authError.value = ''
  token.value = ''
  currentUser.value = null
  conversationDetailRequests.clear()
  conversations.value = []
  statusText.value = '未连接'
  showLogin('你已安全退出，当前会话中的敏感信息已清理。')
  void detectAuthentication(false)
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
    mcpServers.value = await api.listMcpProfiles()
  } catch (error) {
    notifyError(error)
  }
}

async function loadSkillCatalog(): Promise<void> {
  try {
    skillCatalog.value = await api.listSkills()
  } catch (error) {
    notifyError(error)
  }
}

function selectLlm(index: number): void {
  const profile = llmProfiles.value[index]
  if (!profile) return
  selectedLlmIndex.value = index
  llmDraft.value = { ...profile, modelIds: [...(profile.modelIds || [])] }
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
    await reloadAgentCatalog()
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
    await reloadAgentCatalog()
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

async function reloadAgentCatalog(): Promise<void> {
  try {
    agents.value = await api.listAgents()
  } catch (error) {
    notifyError(error)
  }
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

async function refreshConversations(showError = true): Promise<void> {
  try {
    const refreshed = await api.listConversations()
    mergeConversationList(refreshed)
  } catch (error) {
    if (showError) notifyError(error)
  }
}

function mergeConversationList(refreshed: ConversationRecord[]): void {
  conversations.value = mergeConversationRecords(
    conversations.value,
    refreshed,
    new Set(conversationStreams.activeConversationIds()),
    selectedConversation.value?.conversationId,
  )
}

function replaceConversation(detail: ConversationRecord, previousConversationId = detail.conversationId): void {
  const selectionMatches = selectionMatchesConversation(
    selectedConversation.value?.conversationId,
    detail.conversationId,
    previousConversationId,
  )
  conversations.value = replaceConversationRecord(conversations.value, detail, previousConversationId)
  if (selectionMatches) {
    selectedConversation.value = detail
    syncConversationModel(detail)
    sessionStorage.setItem(selectedConversationStorageKey, detail.conversationId)
  }
}

async function restoreSelectedConversation(): Promise<void> {
  if (selectedConversation.value) return
  const storedConversationId = sessionStorage.getItem(selectedConversationStorageKey)
  const stored = conversations.value.find(item => item.conversationId === storedConversationId)
  if (stored) await selectConversation(stored)
}

async function selectConversation(item: ConversationRecord): Promise<void> {
  selectedConversation.value = item
  syncConversationModel(item)
  sessionStorage.setItem(selectedConversationStorageKey, item.conversationId)
  selectedAgentId.value = item.agentId || selectedAgentId.value
  if (item.messages?.length) {
    await hydrateFilePreviews(item)
    return
  }
  const requestId = crypto.randomUUID()
  conversationDetailRequests.set(item.conversationId, requestId)
  try {
    const detail = await api.getConversation(item.conversationId)
    await hydrateFilePreviews(detail)
    if (conversationDetailRequests.get(item.conversationId) !== requestId
      || conversationStreams.isStreaming(item.conversationId)) return
    replaceConversation(detail, item.conversationId)
  } catch (error) {
    if (conversationDetailRequests.get(item.conversationId) === requestId
      && selectedConversation.value?.conversationId === item.conversationId) notifyError(error)
  } finally {
    if (conversationDetailRequests.get(item.conversationId) === requestId) {
      conversationDetailRequests.delete(item.conversationId)
    }
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
  sessionStorage.removeItem(selectedConversationStorageKey)
  message.value = ''
  pendingFiles.value = []
  selectedModelKey.value = ''
  modelScope.value = 'conversation'
  conversationModelDirty.value = false
}

function handleAgentChange(): void {
  // 切换 Agent 时保留当前会话与输入内容：实际场景中 Agent 可随时切换。
  config.value = null
  agentMcpIds.value = []
  mcpServers.value = []
  mcpBindingOptions.value = []
  selectedMcpIndex.value = -1
  skillBindingOptions.value = []
  skillDraft.value = { enabledSkills: [], instances: [] }
  ragInstances.value = []
  selectedRagIndex.value = -1
  selectedModelKey.value = ''
  modelScope.value = 'conversation'
  conversationModelDirty.value = false
}

function modelKey(selection?: LlmModelSelection | null): string {
  return selection ? `${selection.provider}::${selection.modelId}` : ''
}

function parseModelKey(value: string): LlmModelSelection | undefined {
  const separator = value.indexOf('::')
  if (separator <= 0 || separator >= value.length - 2) return undefined
  return { provider: value.slice(0, separator), modelId: value.slice(separator + 2) }
}

function syncConversationModel(conversation?: ConversationRecord | null): void {
  selectedModelKey.value = modelKey(conversation?.modelOverride)
  modelScope.value = 'conversation'
  conversationModelDirty.value = false
}

function updateSelectedModelKey(value: string): void {
  selectedModelKey.value = value
  conversationModelDirty.value = value !== modelKey(selectedConversation.value?.modelOverride)
  if (!value) modelScope.value = 'conversation'
}

function updateModelScope(value: 'conversation' | 'message'): void {
  modelScope.value = value
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
  const conversationId = selectedConversation.value?.conversationId
  if (conversationId) conversationStreams.cancelConversation(conversationId, 'user')
}

async function send(): Promise<void> {
  const content = message.value.trim()
  const hasFiles = pendingFiles.value.length > 0
  if ((!content && !hasFiles) || !selectedAgentId.value || selectedConversationStreaming.value) return
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
  const selectedModel = parseModelKey(selectedModelKey.value)
  const requestedModelScope = selectedModel && modelScope.value === 'message'
    ? 'message'
    : conversationModelDirty.value ? 'conversation' : undefined
  const requestedModel = requestedModelScope ? selectedModel : undefined
  if (requestedModelScope === 'conversation') local.modelOverride = selectedModel || null
  if (isNewConversation) {
    local.messages = []
    local.messageCount = 0
    selectedConversation.value = local
    conversations.value = [local, ...conversations.value.filter(item => item.conversationId !== local.conversationId)]
  }
  const conversation = selectedConversation.value
  if (!conversation) return
  let conversationId = conversation.conversationId
  // 首次对话不携带任何 conversationId：由引擎生成并在 done 事件回传，前端记录后用于后续消息。
  // 这样 router 对首次消息会执行意图识别，而不是当成续聊直接转发。
  const sendConversationId = isNewConversation ? undefined : conversationId
  const streamState = conversationStreams.start(conversationId)
  const requestId = streamState.requestId
  let streamError: { title?: string; detail?: string; traceId?: string } | undefined
  let flushStream: (() => void) | undefined
  let contentQueue: TypewriterQueue | undefined
  let streamedAssistant: ConversationMessage | undefined
  let receivedDone = false
  let completedAgentId = conversation.agentId
  const assistantContentState = createStreamingAssistantContentState()
  let showedEarlyRoutingNotice = false
  try {
    conversation.messages ||= []
    conversation.status = 'Running'
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
    let reasoning = ''
    let lastFlush = 0
    conversation.messages.push({
      messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
      role: 'assistant', content: '', timestamp: new Date().toISOString(),
    })
    const assistantMessage = conversation.messages[conversation.messages.length - 1]!
    streamedAssistant = assistantMessage
    conversation.messageCount = conversation.messages.length
    contentQueue = createTypewriterQueue(content => {
      assistantMessage.content += content
    })
    flushStream = (): void => {
      contentQueue?.flush()
      assistantMessage.reasoning = reasoning || undefined
      lastFlush = performance.now()
    }
    // 正文按可控节奏逐字推进；思考内容继续节流，避免高频重渲染。
    for await (const event of api.streamChat(
      requestContent,
      requestedAgentId,
      sendConversationId,
      uploaded.map(asset => asset.fileId),
      sendConversationId,
      streamState.controller.signal,
      requestedModel,
      requestedModelScope,
    )) {
      if (event.conversationId && event.conversationId !== conversationId) {
        const previousConversationId = conversationId
        const selectionMatches = selectedConversation.value === conversation
          || selectedConversation.value?.conversationId === previousConversationId
        conversationStreams.remap(requestId, event.conversationId)
        conversationId = event.conversationId
        conversation.conversationId = conversationId
        if (selectionMatches) sessionStorage.setItem(selectedConversationStorageKey, conversationId)
      }
      if (event.type === 'agent_selected') {
        if (isNewConversation && selectedAgentId.value === AUTO_AGENT_ID && event.agentId) {
          completedAgentId = event.agentId
          conversation.agentId = event.agentId
          selectedAgentId.value = event.agentId
          const routed = agents.value.find(agent => agent.agentId === event.agentId)
          ElMessage.info(`已由意图识别路由到 Agent「${routed?.name || event.agentId}」`)
          showedEarlyRoutingNotice = true
        }
      } else if (event.type === 'content') {
        enqueueAssistantContent(assistantContentState, content => contentQueue?.enqueue(content), event.content || '')
      } else if (event.type === 'reasoning') {
        reasoning += event.content || ''
        if (performance.now() - lastFlush > 100) {
          assistantMessage.reasoning = reasoning
          lastFlush = performance.now()
        }
      } else if (event.type === 'tool_call') {
        markAssistantPhaseBoundary(assistantContentState)
        assistantMessage.toolActivities ||= []
        assistantMessage.toolActivities.push({
          name: event.toolName || '工具',
          callId: event.toolCallId,
          arguments: event.toolArguments,
        })
      } else if (event.type === 'done') {
        flushStream?.()
        receivedDone = true
        conversation.status = (event.status || 'Completed') as ConversationRecord['status']
        assistantMessage.tokenUsage = event.usage ?? undefined
        assistantMessage.modelId = event.modelId ?? undefined
      } else if (event.type === 'error') {
        flushStream?.()
        // 捕获流式错误，交给 catch 以独立错误卡片展示；不混入助手内容。
        streamError = {
          title: event.error?.title,
          detail: event.error?.detail || 'Agent 执行失败',
          traceId: event.error?.traceId,
        }
        throw new Error(streamError.detail)
      }
    }
    flushStream?.()
    if (!receivedDone) throw new Error('流连接在完成事件前意外结束')
    try {
      const persisted = await api.getConversation(conversationId)
      await hydrateFilePreviews(persisted)
      persisted.messages = mergeAssistantSnapshot(persisted.messages || [], assistantMessage)
      completedAgentId = persisted.agentId
      replaceConversation(persisted, conversationId)
    } catch (error) {
      if (selectedConversation.value?.conversationId === conversationId) notifyError(error)
    }
    await refreshConversations(false)
    // 初次会话：若后端意图识别将对话路由到了其他 Agent，更新右上角选择器并提示。
    if (!showedEarlyRoutingNotice && isNewConversation && completedAgentId && completedAgentId !== requestedAgentId
      && selectedConversation.value?.conversationId === conversationId) {
      const routed = agents.value.find(agent => agent.agentId === completedAgentId)
      selectedAgentId.value = completedAgentId
      ElMessage.info(`已由意图识别路由到 Agent「${routed?.name || completedAgentId}」`)
    }
  } catch (error) {
    flushStream?.()
    const cancelReason = conversationStreams.getByRequest(requestId)?.cancelReason
    if (cancelReason && cancelReason !== 'network') {
      // Cancellation keeps received content while the server finalizes persistence.
      conversation.status = 'Cancelled'
      conversation.updatedAt = new Date().toISOString()
      conversation.lastMessageAt = conversation.updatedAt
      if (cancelReason !== 'unload') {
        try {
          const persisted = await api.getConversation(conversationId)
          await hydrateFilePreviews(persisted)
          if (streamedAssistant) {
            persisted.messages = mergeAssistantSnapshot(persisted.messages || [], streamedAssistant)
          }
          // 仅当服务端会话已有消息时才替换本地内容；否则保留已接收的片段。
          if (persisted.messages?.length && persisted.status !== 'Running') {
            replaceConversation(persisted, conversationId)
          }
        } catch {
          // The server may not have persisted a newly cancelled conversation yet.
        }
        await refreshConversations(false)
      }
    } else {
      if (!streamError) conversationStreams.cancelRequest(requestId, 'network')
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
      try {
        const persisted = await api.getConversation(conversationId)
        await hydrateFilePreviews(persisted)
        if (streamedAssistant) {
          persisted.messages = mergeAssistantSnapshot(persisted.messages || [], streamedAssistant)
        }
        if (persisted.messages?.length && persisted.status !== 'Running') {
          replaceConversation(persisted, conversationId)
        }
      } catch {
        // Reconnect and conversation detail loading will recover persisted messages later.
      }
      if (selectedConversation.value?.conversationId === conversationId) notifyError(error)
    }
  } finally {
    contentQueue?.clear()
    conversationStreams.finish(requestId)
    if (requestedModelScope === 'message') syncConversationModel(selectedConversation.value)
  }
}

async function deleteConversation(item: ConversationRecord): Promise<void> {
  try {
    await ElMessageBox.confirm('确认删除这个会话吗？', '删除会话', { type: 'warning' })
    conversationDetailRequests.delete(item.conversationId)
    const settled = conversationStreams.cancelConversation(item.conversationId, 'delete')
    if (settled) await settled
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
    const mcpIds = [...(loadedConfig.config.mcp?.enabledServerIds || [])]
    for (const legacy of loadedConfig.config.mcp?.servers || []) {
      if (!mcpIds.some(id => id.toLowerCase() === legacy.name.toLowerCase())) mcpIds.push(legacy.name)
    }
    const [selectedMcps, selectedSkills] = await Promise.all([
      Promise.all(mcpIds.map(id => api.getMcpProfile(id).catch(() => null))),
      Promise.all(loadedConfig.config.skills.enabledSkills.map(id => api.getSkill(id).catch(() => null))),
    ])
    config.value = loadedConfig
    mcpServers.value = selectedMcps.filter((item): item is McpServerConfig => item !== null)
    skillCatalog.value = selectedSkills.filter((item): item is SkillCatalogItem => item !== null)
    agentMcpIds.value = mcpIds
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
  return { name: '', url: '', type: 'Http', protocolVersion: null }
}

function selectMcp(index: number): void {
  const server = mcpServers.value[index]
  if (!server) return
  selectedMcpIndex.value = index
  mcpDraft.value = { ...server }
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
  await Promise.all([loadConfig(agentId), loadLlmProfiles()])
  if (config.value?.config.llm.provider) applyLlmProfile(config.value.config.llm.provider)
  isNewAgent.value = false
  showAgentEditor.value = true
}

function chooseSkillPackage(): void {
  skillPackageInput.value?.click()
}

function openSkillTextEditor(): void {
  editingSkillId.value = ''
  skillEditorMode.value = 'form'
  skillEditorName.value = 'my-skill'
  skillEditorDescription.value = 'Describe what this Skill does'
  skillEditorInstructions.value = '# Instructions\n\n'
  skillMarkdownDraft.value = composeSkillMarkdown()
  showSkillTextEditor.value = true
}

function parseSkillMarkdown(markdown: string): { name: string; description: string; body: string } | null {
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
  return name && description ? { name, description, body: lines.slice(end + 1).join('\n').replace(/^\n/, '') } : null
}

function composeSkillMarkdown(): string {
  return `---\nname: ${skillEditorName.value.trim()}\ndescription: ${skillEditorDescription.value.trim()}\n---\n\n${skillEditorInstructions.value}`
}

function switchSkillEditorMode(): void {
  if (skillEditorMode.value === 'form') {
    skillMarkdownDraft.value = composeSkillMarkdown()
    skillEditorMode.value = 'markdown'
    return
  }
  const parsed = parseSkillMarkdown(skillMarkdownDraft.value)
  if (!parsed) {
    ElMessage.warning('当前 Markdown 无法解析，请修正 frontmatter 或继续使用源码模式')
    return
  }
  skillEditorName.value = parsed.name
  skillEditorDescription.value = parsed.description
  skillEditorInstructions.value = parsed.body
  skillEditorMode.value = 'form'
}

async function editSkill(skill: SkillCatalogItem): Promise<void> {
  try {
    const source = await api.getSkillSource(skill.skillId)
    editingSkillId.value = skill.skillId
    skillMarkdownDraft.value = source.markdown
    const parsed = parseSkillMarkdown(source.markdown)
    if (parsed) {
      skillEditorName.value = parsed.name
      skillEditorDescription.value = parsed.description
      skillEditorInstructions.value = parsed.body
      skillEditorMode.value = 'form'
    } else {
      skillEditorMode.value = 'markdown'
    }
    showSkillTextEditor.value = true
  } catch (error) {
    notifyError(error)
  }
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

async function saveTextSkill(): Promise<void> {
  if (skillEditorMode.value === 'form') skillMarkdownDraft.value = composeSkillMarkdown()
  const frontmatter = parseSkillMarkdown(skillMarkdownDraft.value)
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
  await loadLlmProfiles()
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
    await reloadAgentCatalog()
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
  if (!mcpDraft.value.url.trim()) {
    notifyError(new Error('请输入 MCP URL'))
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
    url: mcpDraft.value.url.trim(),
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
  }
  if (name === 'rag') void loadConfig()
}

onMounted(async () => {
  applyTheme()
  window.addEventListener('beforeunload', handlePageUnload)
  window.addEventListener(AUTH_FAILURE_EVENT, handleAuthenticationFailure)
  const hasCallback = new URLSearchParams(window.location.search).has('code')
    || new URLSearchParams(window.location.search).has('error')
  if (hasCallback) {
    const parameters = cleanAuthorizationCallbackUrl()
    authView.value = 'restoring'
    authLoading.value = true
    try {
      await loadAuthConfig()
      if (!authConfig.value) throw new Error('Authentication configuration unavailable')
      const returnHash = await completeOidcLogin(authConfig.value, parameters)
      await establishSession(returnHash)
    } catch {
      clearAuthState()
      authError.value = '企业登录未完成或回调已失效，请重新登录。'
      authView.value = 'login'
    } finally {
      authLoading.value = false
    }
    return
  }
  if (window.location.hash === LOGIN_HASH) clearAuthentication()
  await detectAuthentication(window.location.hash !== LOGIN_HASH)
})

onBeforeUnmount(() => {
  window.removeEventListener(AUTH_FAILURE_EVENT, handleAuthenticationFailure)
})

function handlePageUnload(): void {
  conversationStreams.cancelAll('unload')
}

onBeforeUnmount(() => {
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
          <ChatMessages :messages="currentMessages" :loading="loadingConversation" :current-user="currentUser" :streaming="selectedConversationStreaming" @suggest="message = $event" @download="downloadFile" />
          <MessageComposer :model-value="message" :endpoint-url="activeEndpointUrl" :endpoint-label="activeEndpointLabel" :selected-agent-id="selectedAgentId" :loading="selectedConversationStreaming" :pending-files="pendingFiles" :available-models="availableModels" :selected-model-key="selectedModelKey" :model-scope="modelScope" @update:model-value="message = $event" @update:selected-model-key="updateSelectedModelKey" @update:model-scope="updateModelScope" @files-change="handleFilesChange" @retry-file="retryPendingFile" @send="send" @stop="stopStreaming" />
        </section>
        <aside class="context-panel">
          <div class="context-panel-head"><span class="context-label">INSPECTOR</span><button class="panel-collapse-btn" type="button" aria-label="收起上下文面板" title="收起" @click="toggleContext">›</button></div>
          <section><span class="context-label">ROUTING</span><strong>{{ routeMode }}</strong><p>{{ connectionMode === 'router' && selectedAgentId === AUTO_AGENT_ID ? '由意图识别 Agent 分析请求并选择目标。' : (selectedAgent?.description || selectedAgentId) }}</p><dl><div><dt>Agent</dt><dd>{{ connectionMode === 'router' && selectedAgentId === AUTO_AGENT_ID ? '由模型选择' : (selectedAgent?.name || selectedAgentId) }}</dd></div><div><dt>协议</dt><dd>{{ selectedAgent?.apiFormat || (connectionMode === 'router' ? '自动' : '—') }}</dd></div></dl></section>
          <section><span class="context-label">IDENTITY</span><dl><div><dt>用户</dt><dd>{{ currentUser?.userId || 'Guest' }}</dd></div><div><dt>租户</dt><dd>{{ currentUser?.tenantId || tenantId || '—' }}</dd></div><div><dt>{{ activeEndpointLabel }}</dt><dd :title="activeEndpointUrl">{{ activeEndpointHost }}</dd></div></dl></section>
          <section><span class="context-label">CONVERSATION</span><dl><div><dt>消息</dt><dd>{{ currentMessages.length }}</dd></div><div><dt>状态</dt><dd>{{ conversationStatusText }}</dd></div><div><dt>ID</dt><dd class="truncate" :title="selectedConversation?.conversationId">{{ selectedConversation?.conversationId || '尚未创建' }}</dd></div></dl><div class="conversation-usage"><span>会话累计 Token</span><template v-if="currentUsageSummary.available && currentUsageSummary.usage"><strong>{{ formatTokenCount(currentUsageSummary.usage.totalTokens) }}</strong><small>输入 {{ formatTokenCount(currentUsageSummary.usage.promptTokens) }} · 输出 {{ formatTokenCount(currentUsageSummary.usage.completionTokens) }}</small></template><template v-else><strong class="unavailable">暂不可用</strong><small>Provider 未返回完整 usage</small></template></div></section>
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
            <el-descriptions :column="2" border class="identity-status"><el-descriptions-item label="当前用户">{{ currentUser?.userId || '未连接' }}</el-descriptions-item><el-descriptions-item label="当前租户">{{ currentUser?.tenantId || tenantId || '未识别' }}</el-descriptions-item><el-descriptions-item label="认证状态">{{ currentUser?.isAuthenticated ? '已认证' : '未认证' }}</el-descriptions-item><el-descriptions-item label="认证模式">{{ authConfig?.mode || '未知' }}</el-descriptions-item></el-descriptions>
            <el-alert v-if="authConfig?.mode === 'Basic'" title="当前 Basic 模式仅用于 Development 联调，不校验真实密码，严禁用于生产环境。" type="warning" :closable="false" />
            <el-alert v-else title="身份认证由企业 IdP 完成；角色、Agent ACL 与租户授权继续由服务端策略独立判定。" type="info" :closable="false" />
            <div class="button-row"><el-button type="danger" plain @click="logout">退出登录并清理会话</el-button></div>
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
            <el-table :data="llmProfiles" class="capability-table" empty-text="还没有大模型供应商"><el-table-column label="名称" min-width="140"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="协议" width="160"><template #default="scope"><el-tag size="small" round>{{ scope.row.format }}</el-tag></template></el-table-column><el-table-column label="可切换模型" min-width="120"><template #default="scope">{{ scope.row.modelIds?.length || 0 }} 个</template></el-table-column><el-table-column label="Endpoint" min-width="200" show-overflow-tooltip><template #default="scope">{{ scope.row.endpoint }}</template></el-table-column><el-table-column label="API Key" min-width="120"><template #default="scope">{{ scope.row.apiKey ? '••••••••' : '未配置' }}</template></el-table-column><el-table-column label="操作" width="160" fixed="right"><template #default="scope"><el-button link type="primary" @click="editLlm(scope.$index)">编辑</el-button><el-button link @click="selectLlm(scope.$index); testLlm(); showLlmEditor = true">测试</el-button><el-button link type="danger" @click="selectLlm(scope.$index); deleteLlm()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="MCP 配置" name="mcp">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MCP CATALOG</span><h3>MCP 配置</h3><p>独立维护远程 MCP Server；Agent 页面只选择已注册的 Server，不在 Agent 中复制 Endpoint。</p></div><div class="section-actions"><el-button @click="loadMcpProfiles">刷新</el-button><el-button type="primary" plain @click="newMcp">新增 MCP</el-button></div></div>
            <el-table :data="mcpServers" class="capability-table" empty-text="还没有 MCP 配置"><el-table-column label="名称" min-width="160"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.url || '未配置' }}</template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="selectMcp(scope.$index); showMcpEditor = true">编辑</el-button><el-button link @click="selectMcp(scope.$index); testMcp(); showMcpEditor = true">测试</el-button><el-button link type="danger" @click="removeMcp(scope.$index)">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Skill 配置" name="skill">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">SKILL CATALOG</span><h3>Skill 配置</h3><p>独立维护官方 Skill 目录；Agent 页面只选择 Skill ID，绑定关系保存到 Agent 配置。</p></div><div class="section-actions"><input ref="skillPackageInput" type="file" hidden accept=".zip,.md" @change="uploadSkillPackage" /><el-button @click="loadSkillCatalog">刷新</el-button><el-button :loading="uploadingSkill" type="primary" plain @click="chooseSkillPackage">上传 ZIP / MD</el-button><el-button type="primary" plain @click="openSkillTextEditor">手动填写</el-button></div></div>
            <el-table :data="skillCatalog" class="capability-table" empty-text="还没有 Skill"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.skillId }}</small></template></el-table-column><el-table-column label="说明" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.description }}</template></el-table-column><el-table-column label="内容" width="120"><template #default="scope">{{ scope.row.resourceCount || 0 }} 资源</template></el-table-column><el-table-column label="来源" width="150"><template #default="scope">{{ scope.row.packageFileName }}</template></el-table-column><el-table-column label="操作" width="170" fixed="right"><template #default="scope"><el-button link type="primary" @click="editSkill(scope.row)">编辑</el-button><el-button link type="danger" @click="deleteSkillCatalog(scope.row)">删除</el-button></template></el-table-column></el-table>
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
          <article class="binding-group"><div class="binding-group-heading"><div><strong>MCP</strong><small>通过选择窗口绑定 MCP，当前仅显示已绑定项</small></div><el-button link type="primary" :loading="loadingMcpBindingOptions" @click="openMcpBindingPicker">选择 MCP</el-button></div><div v-if="boundMcpServers.length" class="binding-list"><div v-for="server in boundMcpServers" :key="server.name" class="binding-item"><span class="binding-icon mcp-avatar">M</span><div><strong>{{ server.name }}</strong><small>{{ server.type }} · {{ server.url || '配置不存在' }}</small></div><el-button link type="danger" @click="removeMcpBinding(server.name)">移除</el-button></div></div><div v-else class="binding-empty">尚未绑定 MCP，请点击“选择 MCP”。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>Skill</strong><small>通过选择窗口绑定 Skill，当前仅显示已绑定项</small></div><el-button link type="primary" :loading="loadingSkillBindingOptions" @click="openSkillBindingPicker">选择 Skill</el-button></div><div v-if="boundSkills.length" class="binding-list"><div v-for="skill in boundSkills" :key="skill.skillId" class="binding-item"><span class="binding-icon skill-avatar">S</span><div><strong>{{ skill.name || '未命名 Skill' }}</strong><small>{{ skill.skillId }}</small></div><el-button link type="danger" @click="removeSkillBinding(skill.skillId)">移除</el-button></div></div><div v-else class="binding-empty">尚未绑定 Skill，请点击“选择 Skill”。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>RAG</strong><small>知识检索数据源</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('rag')">管理 RAG</el-button></div><div v-if="ragInstances.length" class="binding-list"><label v-for="rag in ragInstances" :key="rag.id" class="binding-item binding-check-item"><span class="binding-icon rag-avatar">R</span><div><strong>{{ rag.name || rag.id }}</strong><small>{{ rag.type }} · {{ rag.collectionName || '默认数据集' }}</small></div><el-checkbox :model-value="isRagEnabled(rag.id)" @change="toggleRagBinding(rag, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 RAG，去 RAG 表格中新增。</div></article>
        </div>
      </section>
    </div>
    <template #footer><el-button @click="showAgentEditor = false">取消</el-button><el-button type="primary" :loading="savingConfig" @click="saveConfig">保存 Agent 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 MCP" width="min(760px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="mcpBindingOptions" max-height="440" empty-text="还没有可绑定的 MCP"><el-table-column label="名称" min-width="170"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.url }}</template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="agentMcpIds.some(id => id.toLowerCase() === scope.row.name.toLowerCase())" @change="toggleMcpBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showMcpBindingPicker = false">完成</el-button></template>
  </el-dialog>

  <el-dialog v-model="showSkillBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 Skill" width="min(820px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="skillBindingOptions" max-height="440" empty-text="还没有可绑定的 Skill"><el-table-column label="名称" min-width="190"><template #default="scope"><strong>{{ scope.row.name || '未命名 Skill' }}</strong><small class="table-subtext">{{ scope.row.skillId }}</small></template></el-table-column><el-table-column label="说明" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.description }}</template></el-table-column><el-table-column label="资源" width="100"><template #default="scope">{{ scope.row.resourceCount || 0 }}</template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="isSkillEnabled(scope.row.skillId)" @change="toggleSkillBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showSkillBindingPicker = false">完成</el-button></template>
  </el-dialog>

  <el-dialog v-model="showLlmEditor" class="editor-dialog" modal-class="editor-overlay" :title="isNewLlm ? '新增大模型配置' : '编辑大模型配置'" width="min(720px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top" class="agent-form-grid"><el-form-item label="配置 ID"><el-input v-model="llmDraft.id" :disabled="!isNewLlm" placeholder="例如 openai-prod" /><small class="form-help">Agent 通过这个 ID 绑定供应商配置。</small></el-form-item><el-form-item label="显示名称"><el-input v-model="llmDraft.name" placeholder="例如 OpenAI 生产环境" /></el-form-item><el-form-item label="API 格式"><el-select v-model="llmDraft.format" class="full-width"><el-option label="OpenAI Chat Completions" value="OpenAIChatCompletions" /><el-option label="OpenAI Responses" value="OpenAIResponses" /><el-option label="Anthropic Messages" value="AnthropicMessages" /></el-select></el-form-item><el-form-item label="Temperature"><el-input-number v-model="llmDraft.temperature" :min="0" :max="2" :step="0.1" :precision="1" controls-position="right" /></el-form-item><el-form-item label="Endpoint"><el-input v-model="llmDraft.endpoint" placeholder="https://api.openai.com/v1" /></el-form-item><el-form-item label="Provider 状态"><el-switch v-model="llmDraft.isEnabled" active-text="可用" inactive-text="停用" /></el-form-item><el-form-item label="可切换模型 ID" class="span-two"><el-select v-model="llmDraft.modelIds" class="full-width" multiple filterable allow-create default-first-option placeholder="输入模型 ID 后按 Enter"><el-option v-for="modelId in llmDraft.modelIds || []" :key="modelId" :label="modelId" :value="modelId" /></el-select><small class="form-help">只有这里发布的模型可用于会话或单次消息切换；Agent 默认模型保持向后兼容。</small></el-form-item><el-form-item label="API Key" class="span-two"><el-input v-model="llmDraft.apiKey" type="password" show-password placeholder="请输入 API Key" /><small class="form-help">密钥仅提交到服务端，接口返回值始终脱敏。</small></el-form-item><el-alert v-if="llmResult" class="span-two" :title="`测试结果：${llmResult.success ? '连接和权限通过' : '连接失败'}${llmResult.statusCode ? ` · HTTP ${llmResult.statusCode}` : ''}`" :description="llmResult.error || `模型 ${llmResult.modelId || '由 Agent 指定'} · 延迟 ${llmResult.latencyMs}ms`" :type="llmResult.success ? 'success' : 'warning'" :closable="false" /></el-form>
    <template #footer><el-button @click="showLlmEditor = false">取消</el-button><el-button :loading="testingLlm" @click="testLlm">测试连接与权限</el-button><el-button type="primary" :loading="savingLlm" :disabled="!llmDraft.id" @click="saveLlm">保存大模型配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top">
      <el-form-item label="名称"><el-input v-model="mcpDraft.name" :disabled="selectedMcpIndex >= 0" placeholder="例如 local-tools" /><small class="form-help">名称同时作为 MCP 配置 ID；编辑时不可修改，避免已有 Agent 绑定失效。</small></el-form-item>
      <el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="Streamable HTTP" value="Http" /><el-option label="Legacy SSE" value="SSE" /></el-select></el-form-item>
      <el-form-item label="MCP 协议版本"><el-select v-model="mcpDraft.protocolVersion" clearable filterable allow-create placeholder="自动协商（推荐）"><el-option label="2026-07-28" value="2026-07-28" /><el-option label="2025-11-25" value="2025-11-25" /><el-option label="2025-06-18" value="2025-06-18" /><el-option label="2025-03-26" value="2025-03-26" /><el-option label="2024-11-05" value="2024-11-05" /></el-select><small class="form-help">留空由官方 SDK 自动协商；指定版本作为最低版本，服务器降级到更早版本时连接会失败。</small></el-form-item>
      <el-form-item label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com/mcp" /></el-form-item>
      <el-alert v-if="mcpResult" :title="`测试结果：${mcpResult.success ? '连接成功' : '连接失败'} · 权限${mcpResult.authorized ? '通过' : '拒绝'}`" :description="mcpResult.error || `协商版本 ${mcpResult.negotiatedProtocolVersion || '未知'} · ${mcpResult.latencyMs}ms · ${mcpResult.toolCount} 个工具`" :type="mcpResult.success ? 'success' : 'warning'" :closable="false" />
    </el-form><template #footer><el-button @click="showMcpEditor = false">取消</el-button><el-button :loading="testingMcp" @click="testMcp">测试连接、版本与权限</el-button><el-button type="primary" :disabled="!mcpDraft.name || !mcpDraft.url" @click="saveMcp">保存 MCP 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showSkillTextEditor" class="editor-dialog" modal-class="editor-overlay" :title="editingSkillId ? '编辑 Markdown Skill' : '新增 Markdown Skill'" width="min(820px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-alert title="可用表单填写 Skill 名称、说明和 Markdown 指令；切换到源码模式后直接编辑完整 SKILL.md。无法解析的 Skill 会自动进入源码模式。" type="info" :closable="false" />
    <div class="skill-editor-toolbar"><el-tag v-if="skillEditorMode === 'markdown'" type="warning" effect="plain">Markdown 源码模式</el-tag><el-button link type="primary" @click="switchSkillEditorMode">{{ skillEditorMode === 'form' ? '切换到 Markdown 源码' : '切换到表单模式' }}</el-button></div>
    <el-form v-if="skillEditorMode === 'form'" label-position="top" class="agent-form-grid">
      <el-form-item label="Skill 名称"><el-input v-model="skillEditorName" placeholder="例如 customer-lookup" /></el-form-item>
      <el-form-item label="Skill 说明"><el-input v-model="skillEditorDescription" placeholder="说明这个 Skill 适用的场景" /></el-form-item>
      <el-form-item label="Markdown 指令" class="span-two"><el-input v-model="skillEditorInstructions" class="skill-markdown-input" type="textarea" :rows="16" spellcheck="false" placeholder="# Instructions" /></el-form-item>
    </el-form>
    <el-input v-else v-model="skillMarkdownDraft" class="skill-markdown-input" type="textarea" :rows="20" spellcheck="false" />
    <template #footer><el-button @click="showSkillTextEditor = false">取消</el-button><el-button type="primary" :loading="uploadingSkill" @click="saveTextSkill">校验并保存 Skill</el-button></template>
  </el-dialog>

  <el-dialog v-model="showRagEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 RAG" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="RAG ID"><el-input v-model="ragDraft.id" placeholder="例如 knowledge-base" /></el-form-item><el-form-item label="名称"><el-input v-model="ragDraft.name" placeholder="例如 企业知识库" /></el-form-item><el-form-item label="类型"><el-select v-model="ragDraft.type"><el-option label="RAGFlow" value="ragflow" /><el-option label="Qdrant" value="qdrant" /></el-select></el-form-item><el-form-item label="Endpoint"><el-input v-model="ragDraft.apiEndpoint" placeholder="https://rag.example.com/api/search" /></el-form-item><el-form-item label="Collection / Dataset"><el-input v-model="ragDraft.collectionName" /></el-form-item><el-form-item label="API Key"><el-input v-model="ragDraft.apiKey" type="password" show-password placeholder="留空则保留已保存的密钥" /></el-form-item><el-form-item label="状态"><el-switch v-model="ragDraft.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="ragResult" :title="`测试结果：${ragResult.success ? '连接成功' : '连接失败'}`" :description="ragResult.error || `HTTP ${ragResult.statusCode || '-'} · 延迟 ${ragResult.latencyMs}ms`" :type="ragResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showRagEditor = false">取消</el-button><el-button :loading="testingRag" @click="testRag">测试连接</el-button><el-button type="primary" :disabled="!ragDraft.id" @click="saveRag">保存 RAG 配置</el-button></template>
  </el-dialog>
</template>
