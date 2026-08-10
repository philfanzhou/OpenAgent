<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getAccessToken, getEngineBaseUrl, getTenantId, makeLocalConversation, setAccessToken, setEngineBaseUrl, setTenantId } from './api'
import ChatHeader from './components/ChatHeader.vue'
import ChatMessages from './components/ChatMessages.vue'
import ChatSidebar from './components/ChatSidebar.vue'
import MessageComposer from './components/MessageComposer.vue'
import { AUTO_AGENT_ID, type AgentConfigEntity, type AgentSummary, type AuthConfig, type ConversationMessage, type ConversationRecord, type CurrentUserContext, type LlmProviderProfile, type LlmTestResult, type McpServerConfig, type McpTestResult, type MessageAttachment, type PendingAttachment, type RagConfig, type RagInstanceConfig, type RagTestResult, type SkillInstanceConfig, type SkillsConfig } from './types'

const engineUrl = ref(getEngineBaseUrl())
const token = ref(getAccessToken())
const tenantId = ref(getTenantId())
const showSettings = ref(!engineUrl.value)
const activeSettings = ref<'engine' | 'llm' | 'agent' | 'mcp' | 'skill' | 'rag'>('engine')
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
const refreshingAgents = ref(false)
const testingMcp = ref(false)
const testingSkill = ref(false)
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
const llmProfiles = ref<LlmProviderProfile[]>([])
const llmDraft = ref<LlmProviderProfile>(createDefaultLlm())
const selectedLlmIndex = ref(-1)
const llmResult = ref<LlmTestResult | null>(null)
const testingLlm = ref(false)
const savingLlm = ref(false)
const showLlmEditor = ref(false)
const isNewLlm = ref(false)
const pendingAttachments = ref<PendingAttachment[]>([])
const themeMode = ref<'light' | 'dark'>(localStorage.getItem('openagent.ui.theme') === 'dark' ? 'dark' : 'light')

const currentMessages = computed(() => selectedConversation.value?.messages || [])
const selectedSkill = computed(() => selectedSkillIndex.value >= 0 ? skillDraft.value.instances[selectedSkillIndex.value] || null : null)
const enabledSkillIds = computed(() => new Set(skillDraft.value.enabledSkills))
const enabledRagIds = computed(() => new Set(config.value?.config.rag?.enabledRagInstanceIds || ragInstances.value.filter(item => item.enabled).map(item => item.id)))
const mcpArgumentsText = computed({
  get: () => (mcpDraft.value.arguments || []).join('\n'),
  set: (value: string) => { mcpDraft.value.arguments = value.split('\n').map(item => item.trim()).filter(Boolean) },
})

const ragEnabledText = computed(() => config.value?.config.rag?.enabled ? '已启用' : '未启用')

function applyTheme(): void {
  document.documentElement.dataset.theme = themeMode.value
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
    modelId: 'gpt-4o',
    endpoint: 'https://api.openai.com/v1',
    apiKey: '',
    temperature: 0.7,
  }
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
    if (selectedAgentId.value !== AUTO_AGENT_ID && !agents.value.some(item => item.agentId === selectedAgentId.value)) {
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
  if (!profile.name.trim() || !profile.modelId.trim() || !profile.endpoint.trim()) return notifyError(new Error('请填写名称、模型 ID 和 Endpoint'))
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
  config.value.config.llm.modelId = profile.modelId
  config.value.config.llm.endpoint = profile.endpoint
  config.value.config.llm.temperature = profile.temperature
}

async function refreshAgents(): Promise<void> {
  refreshingAgents.value = true
  try {
    const refreshed = await api.listAgents()
    agents.value = refreshed
    if (selectedAgentId.value !== AUTO_AGENT_ID && !refreshed.some(item => item.agentId === selectedAgentId.value)) {
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

async function send(): Promise<void> {
  const content = message.value.trim()
  const hasAttachments = pendingAttachments.value.length > 0
  if ((!content && !hasAttachments) || !selectedAgentId.value || loading.value) return
  const requestContent = content || '请处理我上传的附件'
  const isNewConversation = !selectedConversation.value
  const attachments = pendingAttachments.value.map(item => item.file)
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
  conversation.messages ||= []
  conversation.messages.push({
    messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
    role: 'user', content: content || '已上传附件', timestamp: new Date().toISOString(),
    attachments: pendingAttachments.value.map(item => ({
      fileName: item.file.name,
      mediaType: item.file.type || 'application/octet-stream',
      length: item.file.size,
      previewUrl: item.file.type.startsWith('image/') ? URL.createObjectURL(item.file) : undefined,
    } satisfies MessageAttachment)),
  })
  conversation.messageCount = conversation.messages.length
  conversation.updatedAt = new Date().toISOString()
  conversation.lastMessageAt = conversation.updatedAt
  message.value = ''
  pendingAttachments.value = []
  loading.value = true
  let assistant = ''
  const assistantMessage: ConversationMessage = {
    messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
    role: 'assistant', content: '', timestamp: new Date().toISOString(),
  }
  conversation.messages.push(assistantMessage)
  try {
    for await (const event of api.streamChat(requestContent, requestedAgentId, conversationId, attachments)) {
      if (event.type === 'content') {
        assistant += event.content || ''
        assistantMessage.content = assistant
      } else if (event.type === 'tool_call') {
        assistantMessage.toolName = event.toolName
      } else if (event.type === 'error') {
        throw new Error(event.error?.detail || 'Agent 执行失败')
      }
    }
    await refreshConversations()
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
  if (!selectedAgentId.value || selectedAgentId.value === AUTO_AGENT_ID) return
  try {
    config.value = await api.getAgentConfig(selectedAgentId.value)
    mcpServers.value = (config.value.config.mcp?.servers || []).map(item => ({
      ...item,
      arguments: [...(item.arguments || [])],
      environmentVariables: { ...(item.environmentVariables || {}) },
    }))
    skillDraft.value = {
      enabledSkills: [...config.value.config.skills.enabledSkills],
      instances: config.value.config.skills.instances.map(item => ({ ...item, enabled: config.value?.config.skills.enabledSkills.includes(item.skillId) ?? item.enabled })),
    }
    selectedSkillIndex.value = skillDraft.value.instances.length ? 0 : -1
    const enabledRagInstanceIds = new Set(config.value.config.rag?.enabledRagInstanceIds || [])
    ragInstances.value = (config.value.config.rag?.instances || []).map(item => ({ ...item, enabled: enabledRagInstanceIds.size ? enabledRagInstanceIds.has(item.id) : item.enabled }))
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
  await Promise.all([loadConfig(), loadMcp(), loadLlmProfiles()])
  isNewAgent.value = false
  showAgentEditor.value = true
}

async function loadMcp(openEditorIfEmpty = false): Promise<void> {
  if (!selectedAgentId.value || selectedAgentId.value === AUTO_AGENT_ID) return
  try {
    const result = await api.getMcpConfig(selectedAgentId.value)
    mcpServers.value = result.servers || []
    if (selectedMcpIndex.value >= 0 && selectedMcpIndex.value < mcpServers.value.length) selectMcp(selectedMcpIndex.value)
    else if (mcpServers.value.length) selectMcp(0)
    else {
      selectedMcpIndex.value = -1
      mcpDraft.value = createDefaultMcp()
      if (openEditorIfEmpty) newMcp()
    }
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
  config.value.config.mcp = { servers: mcpServers.value.map(item => ({ ...item })) }
  config.value.config.skills = {
    enabledSkills: [...skillDraft.value.enabledSkills],
    instances: skillDraft.value.instances.map(item => ({ ...item })),
  }
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
      { agentId, name: saved.name, description: saved.description, status: saved.status, currentVersion: saved.currentVersion, apiFormat: String(saved.config.llm.format || '') },
    ]
    isNewAgent.value = false
    ElMessage.success('Agent 配置已保存')
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
  if (panel === 'llm') void loadLlmProfiles()
  if (panel === 'agent') {
    void loadConfig()
    void loadLlmProfiles()
  }
  if (panel === 'skill' || panel === 'rag') void loadConfig()
  if (panel === 'mcp') void loadMcp()
}

function handleSettingsTabChange(name: string | number): void {
  if (name === 'llm') void loadLlmProfiles()
  if (name === 'agent') {
    void loadConfig()
    void loadLlmProfiles()
  }
  if (name === 'skill' || name === 'rag') void loadConfig()
  if (name === 'mcp') void loadMcp()
}

onMounted(() => {
  applyTheme()
  if (engineUrl.value) {
    void connect()
  }
})
</script>

<template>
  <el-container class="app-shell">
    <ChatSidebar :conversations="conversations" :selected-conversation-id="selectedConversation?.conversationId" :search="search" :loading="loading" @update:search="search = $event" @new="newConversation" @settings="openSettings('engine')" @refresh="loadWorkspace" @select="selectConversation" @delete="deleteConversation" />

    <el-main class="main-panel">
      <ChatHeader :status-text="statusText" :agents="agents" :selected-agent-id="selectedAgentId" :refreshing-agents="refreshingAgents" :title="selectedConversation?.title || '新对话'" :theme-mode="themeMode" @update:selected-agent-id="selectedAgentId = $event" @agent-change="handleAgentChange" @refresh-agents="refreshAgents" @settings="openSettings('engine')" @toggle-theme="toggleTheme" />

      <section class="chat-card">
        <ChatMessages :messages="currentMessages" :loading="loadingConversation" />
        <MessageComposer :model-value="message" :engine-url="engineUrl" :selected-agent-id="selectedAgentId" :loading="loading" :pending-attachments="pendingAttachments" @update:model-value="message = $event" @attachments-change="pendingAttachments = $event" @send="send" />
      </section>
    </el-main>
  </el-container>

  <el-dialog v-model="showSettings" class="settings-dialog" width="min(1180px, calc(100vw - 40px))" top="3vh" :close-on-click-modal="false" destroy-on-close>
    <template #header>
      <div class="settings-header"><div><span class="eyebrow">WORKSPACE CONTROL</span><h2>工作台设置</h2></div></div>
    </template>
    <div class="settings-body">
      <el-tabs v-model="activeSettings" tab-position="left" class="settings-tabs" @tab-change="handleSettingsTabChange">
        <el-tab-pane label="Engine 连接" name="engine">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CONNECTION</span><h3>连接与身份</h3><p>配置当前工作台要访问的 Engine，设置会保存在本机浏览器中。</p></div><span class="connection-badge" :class="{ online: statusText === '已连接' }"><i />{{ statusText }}</span></div>
            <el-form label-position="top"><el-form-item label="Engine 地址"><el-input v-model="engineUrl" placeholder="http://localhost:5208" /></el-form-item><el-form-item label="租户 ID"><el-input v-model="tenantId" placeholder="可选：用于当前工作台隔离" /></el-form-item></el-form>
            <el-descriptions :column="2" border class="identity-status"><el-descriptions-item label="当前用户">{{ currentUser?.userId || '未连接' }}</el-descriptions-item><el-descriptions-item label="当前租户">{{ currentUser?.tenantId || tenantId || '未识别' }}</el-descriptions-item><el-descriptions-item label="认证状态">{{ currentUser?.isAuthenticated ? '已认证' : '未认证' }}</el-descriptions-item><el-descriptions-item label="登录状态">{{ token ? '已登录（Basic）' : '未登录' }}</el-descriptions-item></el-descriptions>
            <el-alert title="当前仅使用基础账号密码建立身份。后端暂不校验账号密码正确性，具体资源授权后续单独实现。" type="info" :closable="false" />
            <section class="login-card"><div class="login-card-heading"><div><span class="eyebrow">BASIC ACCOUNT</span><h4>登录 Engine</h4></div><span class="login-config-status">{{ authConfig?.mode || 'Basic' }}</span></div><el-form label-position="top" class="login-form"><el-form-item label="账号"><el-input v-model="username" autocomplete="username" placeholder="请输入账号" /></el-form-item><el-form-item label="密码"><el-input v-model="password" type="password" show-password autocomplete="current-password" placeholder="请输入密码" /></el-form-item></el-form><el-button type="primary" :loading="authLoading" :disabled="!authConfig?.password.enabled" @click="loginWithPassword">账号密码登录</el-button><small class="login-hint">当前阶段只要填写账号和密码即可通过；后端不会核对账号是否存在或密码是否正确。</small></section>
            <div class="button-row"><el-button type="primary" @click="connect">保存并连接</el-button><el-button @click="api.health('/health').then(() => ElMessage.success('Live 健康检查通过')).catch(notifyError)">测试 Live</el-button><el-button @click="api.health('/ready').then(() => ElMessage.success('Ready 健康检查通过')).catch(notifyError)">测试 Ready</el-button></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="LLM 配置" name="llm">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MODEL PROVIDERS</span><h3>大模型配置</h3><p>独立维护多条模型供应商配置，Agent 通过 Provider ID 绑定；API Key 只在保存时写入，列表中始终脱敏。</p></div><div class="section-actions"><el-button @click="loadLlmProfiles">刷新</el-button><el-button type="primary" plain @click="newLlm">新增大模型</el-button></div></div>
            <el-table :data="llmProfiles" class="capability-table" empty-text="还没有大模型配置"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="协议" width="190"><template #default="scope"><el-tag size="small" round>{{ scope.row.format }}</el-tag></template></el-table-column><el-table-column label="模型" min-width="150"> <template #default="scope">{{ scope.row.modelId }}</template></el-table-column><el-table-column label="Endpoint" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.endpoint }}</template></el-table-column><el-table-column label="API Key" min-width="190" show-overflow-tooltip><template #default="scope">{{ scope.row.apiKey || '未配置' }}</template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="editLlm(scope.$index)">编辑</el-button><el-button link @click="selectLlm(scope.$index); testLlm(); showLlmEditor = true">测试</el-button><el-button link type="danger" @click="selectLlm(scope.$index); deleteLlm()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Agent 配置" name="agent">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">AGENT RUNTIME</span><h3>Agent 配置</h3><p>Agent 以卡片方式管理，点击编辑后在独立窗口配置模型与运行参数。</p></div><div class="section-actions"><el-button @click="refreshAgents" :loading="refreshingAgents">刷新 Agent</el-button><el-button type="primary" plain @click="createAgent">新增 Agent</el-button></div></div>
            <div class="agent-card-grid"><article v-for="agent in agents" :key="agent.agentId" class="agent-card"><div class="agent-card-top"><span class="resource-avatar agent-avatar">{{ (agent.name || agent.agentId).slice(0, 1) }}</span><span class="resource-status" /></div><h4>{{ agent.name || agent.agentId }}</h4><p>{{ agent.description || agent.agentId }}</p><div class="agent-card-meta"><span>{{ agent.apiFormat || '未配置模型' }}</span><span>v{{ agent.currentVersion || 'draft' }}</span></div><el-button type="primary" plain @click="editAgent(agent.agentId)">编辑配置</el-button></article><button class="agent-card agent-card-add" @click="createAgent"><span>＋</span><strong>新增 Agent</strong><small>创建独立运行配置</small></button><div v-if="!agents.length" class="resource-empty">还没有 Agent</div></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="MCP 绑定" name="mcp">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>MCP 绑定</h3><p>以表格查看每条 MCP 的传输方式、地址和测试状态，连接由 Engine 服务端执行。</p></div><div class="section-actions"><el-button type="primary" plain @click="newMcp">新增 MCP</el-button></div></div>
            <el-table :data="mcpServers" class="capability-table" empty-text="还没有绑定 MCP"><el-table-column label="名称" min-width="170"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="传输" width="110"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址 / 命令" min-width="250" show-overflow-tooltip><template #default="scope">{{ scope.row.url || [scope.row.command, ...(scope.row.arguments || [])].filter(Boolean).join(' ') || '未配置' }}</template></el-table-column><el-table-column label="状态" width="100"><template #default="scope"><span class="table-status"><i />已绑定</span></template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="selectMcp(scope.$index); showMcpEditor = true">编辑</el-button><el-button link @click="testMcpRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectMcp(scope.$index); deleteMcp()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Skill 绑定" name="skill">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>Skill 绑定</h3><p>以表格查看每条 Skill 的 ID、状态和说明，支持逐条编辑与测试。</p></div><div class="section-actions"><el-button type="primary" plain @click="newSkill">新增 Skill</el-button></div></div>
            <el-table :data="skillDraft.instances" class="capability-table" empty-text="还没有绑定 Skill"><el-table-column label="Skill" min-width="180"><template #default="scope"><strong>{{ scope.row.name || '未命名 Skill' }}</strong><small class="table-subtext">{{ scope.row.skillId || '未设置 ID' }}</small></template></el-table-column><el-table-column label="来源 / 类型" width="150"><template #default="scope">{{ scope.row.source || 'Local' }}<span v-if="scope.row.type"> · {{ scope.row.type }}</span></template></el-table-column><el-table-column label="状态" width="110"><template #default="scope"><span class="table-status" :class="{ muted: !isSkillEnabled(scope.row.skillId) }"><i />{{ isSkillEnabled(scope.row.skillId) ? '已绑定' : '未绑定' }}</span></template></el-table-column><el-table-column label="说明" min-width="250" show-overflow-tooltip><template #default="scope">{{ scope.row.description || '—' }}</template></el-table-column><el-table-column label="操作" width="170" fixed="right"><template #default="scope"><el-button link type="primary" @click="editSkill(scope.$index)">编辑</el-button><el-button link @click="testSkillRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectSkill(scope.$index); removeSkill()">删除</el-button></template></el-table-column></el-table>
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

  <el-dialog v-model="showAgentEditor" class="editor-dialog agent-editor-dialog" width="min(920px, calc(100vw - 32px))" append-to-body destroy-on-close>
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
          <el-form-item label="大模型配置"><el-select v-model="config.config.llm.provider" class="full-width" filterable allow-create placeholder="选择已维护的配置或直接输入 Provider ID" @change="applyLlmProfile"><el-option v-for="profile in llmProfiles" :key="profile.id" :label="`${profile.name} · ${profile.modelId}`" :value="profile.id" /></el-select><small class="form-help">推荐先在“LLM 配置”中维护供应商，再在这里绑定 Provider ID。</small></el-form-item>
          <el-form-item label="模型 ID"><el-input v-model="config.config.llm.modelId" placeholder="例如 gpt-4o" /></el-form-item>
          <el-form-item label="API 格式"><el-select v-model="config.config.llm.format" class="full-width"><el-option label="OpenAI Chat Completions" value="OpenAIChatCompletions" /><el-option label="OpenAI Responses" value="OpenAIResponses" /><el-option label="Anthropic Messages" value="AnthropicMessages" /></el-select></el-form-item>
          <el-form-item label="Temperature"><el-input-number v-model="config.config.llm.temperature" :min="0" :max="2" :step="0.1" :precision="1" controls-position="right" /><small class="form-help">0 更稳定，2 更有创造性。</small></el-form-item>
          <el-form-item label="Endpoint" class="span-two"><el-input v-model="config.config.llm.endpoint" placeholder="https://api.example.com/v1" /></el-form-item>
          <el-form-item label="API Key" class="span-two"><el-input v-model="config.config.llm.apiKey" type="password" show-password placeholder="留空则保留已保存的密钥" /></el-form-item>
        </el-form>
      </section>

      <section class="agent-editor-section">
        <div class="agent-editor-section-heading"><div><span class="eyebrow">CAPABILITY BINDINGS</span><h4>能力绑定</h4><p>当前 Agent 的 MCP、Skill、RAG 以卡片展示；勾选即可启用或停用 Skill 与 RAG。</p></div><span class="editor-section-index">03</span></div>
        <div class="binding-groups">
          <article class="binding-group"><div class="binding-group-heading"><div><strong>MCP</strong><small>服务端连接的工具集合</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('mcp')">管理 MCP</el-button></div><div v-if="mcpServers.length" class="binding-list"><div v-for="server in mcpServers" :key="server.name" class="binding-item"><span class="binding-icon mcp-avatar">M</span><div><strong>{{ server.name }}</strong><small>{{ server.type }} · {{ server.url || server.command || 'Stdio' }}</small></div><el-tag size="small" round type="success">已绑定</el-tag></div></div><div v-else class="binding-empty">还没有 MCP，去 MCP 表格中新增并绑定。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>Skill</strong><small>可复用的业务能力</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('skill')">管理 Skill</el-button></div><div v-if="skillDraft.instances.length" class="binding-list"><label v-for="skill in skillDraft.instances" :key="skill.skillId" class="binding-item binding-check-item"><span class="binding-icon skill-avatar">S</span><div><strong>{{ skill.name || '未命名 Skill' }}</strong><small>{{ skill.skillId || '未设置 ID' }}</small></div><el-checkbox :model-value="isSkillEnabled(skill.skillId)" @change="toggleSkillBinding(skill, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 Skill，去 Skill 表格中新增。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>RAG</strong><small>知识检索数据源</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('rag')">管理 RAG</el-button></div><div v-if="ragInstances.length" class="binding-list"><label v-for="rag in ragInstances" :key="rag.id" class="binding-item binding-check-item"><span class="binding-icon rag-avatar">R</span><div><strong>{{ rag.name || rag.id }}</strong><small>{{ rag.type }} · {{ rag.collectionName || '默认数据集' }}</small></div><el-checkbox :model-value="isRagEnabled(rag.id)" @change="toggleRagBinding(rag, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 RAG，去 RAG 表格中新增。</div></article>
        </div>
      </section>
    </div>
    <template #footer><el-button @click="showAgentEditor = false">取消</el-button><el-button type="primary" :loading="savingConfig" @click="saveConfig">保存 Agent 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showLlmEditor" class="editor-dialog" :title="isNewLlm ? '新增大模型配置' : '编辑大模型配置'" width="min(720px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top" class="agent-form-grid"><el-form-item label="配置 ID"><el-input v-model="llmDraft.id" :disabled="!isNewLlm" placeholder="例如 openai-prod" /><small class="form-help">Agent 通过这个 ID 绑定大模型配置。</small></el-form-item><el-form-item label="显示名称"><el-input v-model="llmDraft.name" placeholder="例如 OpenAI 生产环境" /></el-form-item><el-form-item label="API 格式"><el-select v-model="llmDraft.format" class="full-width"><el-option label="OpenAI Chat Completions" value="OpenAIChatCompletions" /><el-option label="OpenAI Responses" value="OpenAIResponses" /><el-option label="Anthropic Messages" value="AnthropicMessages" /></el-select></el-form-item><el-form-item label="模型 ID"><el-input v-model="llmDraft.modelId" placeholder="例如 gpt-4o" /></el-form-item><el-form-item label="Temperature"><el-input-number v-model="llmDraft.temperature" :min="0" :max="2" :step="0.1" :precision="1" controls-position="right" /></el-form-item><el-form-item label="Endpoint"><el-input v-model="llmDraft.endpoint" placeholder="https://api.openai.com/v1" /></el-form-item><el-form-item label="API Key" class="span-two"><el-input v-model="llmDraft.apiKey" type="text" placeholder="请输入 API Key" /><small class="form-help">API Key 直接显示，便于确认和修改当前大模型配置。</small></el-form-item><el-alert v-if="llmResult" class="span-two" :title="`测试结果：${llmResult.success ? '连接和权限通过' : '连接失败'}${llmResult.statusCode ? ` · HTTP ${llmResult.statusCode}` : ''}`" :description="llmResult.error || `模型 ${llmResult.modelId || llmDraft.modelId} · 延迟 ${llmResult.latencyMs}ms`" :type="llmResult.success ? 'success' : 'warning'" :closable="false" /></el-form>
    <template #footer><el-button @click="showLlmEditor = false">取消</el-button><el-button :loading="testingLlm" @click="testLlm">测试连接与权限</el-button><el-button type="primary" :loading="savingLlm" :disabled="!llmDraft.id" @click="saveLlm">保存大模型配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpEditor" class="editor-dialog" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="名称"><el-input v-model="mcpDraft.name" placeholder="例如 local-tools" /></el-form-item><el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="HTTP" value="Http" /><el-option label="SSE" value="SSE" /><el-option label="Stdio（服务端执行）" value="Stdio" /></el-select></el-form-item><el-form-item v-if="mcpDraft.type !== 'Stdio'" label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com" /></el-form-item><template v-else><el-form-item label="Command"><el-input v-model="mcpDraft.command" placeholder="例如 node" /></el-form-item><el-form-item label="Arguments（每行一个）"><el-input v-model="mcpArgumentsText" type="textarea" :rows="3" /></el-form-item><el-form-item label="Working Directory"><el-input v-model="mcpDraft.workingDirectory" /></el-form-item></template><el-alert v-if="mcpResult" :title="`测试结果：${mcpResult.success ? '连接成功' : '连接失败'} · 权限${mcpResult.authorized ? '通过' : '拒绝'}`" :description="mcpResult.error || `延迟 ${mcpResult.latencyMs}ms，发现 ${mcpResult.toolCount} 个工具`" :type="mcpResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showMcpEditor = false">取消</el-button><el-button :loading="testingMcp" @click="testMcp">测试连接与权限</el-button><el-button type="primary" :disabled="!mcpDraft.name" @click="saveMcp">保存 MCP 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showSkillEditor" class="editor-dialog" title="编辑 Skill" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form v-if="selectedSkill" label-position="top"><el-form-item label="Skill ID"><el-input v-model="selectedSkill.skillId" placeholder="例如 weather" /></el-form-item><el-form-item label="名称"><el-input v-model="selectedSkill.name" placeholder="例如 天气查询" /></el-form-item><el-form-item label="说明"><el-input v-model="selectedSkill.description" type="textarea" :rows="3" /></el-form-item><el-form-item label="纳入 Agent 能力"><el-switch :model-value="isSkillEnabled(selectedSkill.skillId)" active-text="已绑定并启用" inactive-text="未绑定" @change="toggleSkillBinding(selectedSkill, Boolean($event))" /></el-form-item><el-form-item label="状态"><el-switch v-model="selectedSkill.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="skillResult" :title="skillResult.success ? 'Skill 配置测试通过' : 'Skill 配置测试失败'" :description="`已启用 ${skillResult.enabledCount || 0} 条，实例 ${skillResult.instanceCount || 0} 条`" :type="skillResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showSkillEditor = false">取消</el-button><el-button :loading="testingSkill" @click="testSkills">测试 Skill 配置</el-button><el-button type="primary" @click="saveSkills">保存 Skill 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showRagEditor" class="editor-dialog" title="编辑 RAG" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="RAG ID"><el-input v-model="ragDraft.id" placeholder="例如 knowledge-base" /></el-form-item><el-form-item label="名称"><el-input v-model="ragDraft.name" placeholder="例如 企业知识库" /></el-form-item><el-form-item label="类型"><el-select v-model="ragDraft.type"><el-option label="RAGFlow" value="ragflow" /><el-option label="Qdrant" value="qdrant" /></el-select></el-form-item><el-form-item label="Endpoint"><el-input v-model="ragDraft.apiEndpoint" placeholder="https://rag.example.com/api/search" /></el-form-item><el-form-item label="Collection / Dataset"><el-input v-model="ragDraft.collectionName" /></el-form-item><el-form-item label="API Key"><el-input v-model="ragDraft.apiKey" type="password" show-password placeholder="留空则保留已保存的密钥" /></el-form-item><el-form-item label="状态"><el-switch v-model="ragDraft.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="ragResult" :title="`测试结果：${ragResult.success ? '连接成功' : '连接失败'}`" :description="ragResult.error || `HTTP ${ragResult.statusCode || '-'} · 延迟 ${ragResult.latencyMs}ms`" :type="ragResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showRagEditor = false">取消</el-button><el-button :loading="testingRag" @click="testRag">测试连接</el-button><el-button type="primary" :disabled="!ragDraft.id" @click="saveRag">保存 RAG 配置</el-button></template>
  </el-dialog>
</template>

<style>
.message-attachments { display: flex; flex-wrap: wrap; gap: 5px; margin-bottom: 7px; }
.message-attachments span { display: inline-flex; align-items: center; padding: 4px 7px; color: #5d54e8; background: #fff; border: 1px solid #dfe2ff; border-radius: 6px; font-size: 11px; }
</style>
