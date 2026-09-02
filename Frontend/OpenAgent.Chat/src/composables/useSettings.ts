import { computed, ref, type Ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getTenantId } from '../api'
import { randomUuid } from '../browserCrypto'
import {
  AUTO_AGENT_ID,
  type AgentConfigEntity,
  type AgentSummary,
  type ConnectionMode,
  type LlmProviderProfile,
  type LlmTestResult,
  type McpServerConfig,
  type McpTestResult,
  type RagInstanceConfig,
  type RagTestResult,
  type SkillCatalogItem,
  type SkillInstanceConfig,
  type SkillsConfig,
} from '../types'

export type SettingsPanel = 'gateway' | 'health' | 'llm' | 'mcp' | 'skill' | 'agent' | 'rag'

interface SettingsOptions {
  agents: Ref<AgentSummary[]>
  selectedAgentId: Ref<string>
  selectedLlmProfileId: Ref<string>
  connectionMode: Ref<ConnectionMode>
  routerUrl: Ref<string>
  engineUrl: Ref<string>
  notifyError: (error: unknown) => void
}

export function parseSkillMarkdown(markdown: string): { name: string; description: string; body: string } | null {
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

export function composeSkillMarkdown(name: string, description: string, instructions: string): string {
  return `---\nname: ${name.trim()}\ndescription: ${description.trim()}\n---\n\n${instructions}`
}

function createDefaultLlm(): LlmProviderProfile {
  return {
    id: '',
    name: '',
    format: 'OpenAIChatCompletions',
    modelId: 'gpt-4o',
    contextWindowTokens: 128000,
    endpoint: 'https://api.openai.com/v1',
    apiKey: '',
    temperature: 0.7,
    modality: 'Text',
  }
}

function createDefaultMcp(): McpServerConfig {
  return { name: '', url: '', type: 'Http', protocolVersion: null }
}

function createDefaultRag(): RagInstanceConfig {
  return { id: '', name: '', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: '', apiKeySecretRef: '', apiKey: '' }
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
      mcp: { servers: [] },
      rag: { enabled: false, enabledRagInstanceIds: [], instances: [] },
      skills: { enabledSkills: [], instances: [] },
      maxTurns: 50,
    },
  }
}

export function useSettings(options: SettingsOptions) {
  const showSettings = ref(!(options.connectionMode.value === 'router' ? options.routerUrl.value : options.engineUrl.value))
  const activeSettings = ref<SettingsPanel>('gateway')
  const savingConfig = ref(false)
  const refreshingAgents = ref(false)
  const testingMcp = ref(false)
  const uploadingSkill = ref(false)
  const testingRag = ref(false)
  const config = ref<AgentConfigEntity | null>(null)
  const showAgentEditor = ref(false)
  const isNewAgent = ref(false)
  const showMcpEditor = ref(false)
  const showRagEditor = ref(false)
  const mcpDraft = ref<McpServerConfig>(createDefaultMcp())
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
  const ragDraft = ref<RagInstanceConfig>(createDefaultRag())
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
  const ragEnabledText = computed(() => config.value?.config.rag?.enabled ? '已启用' : '未启用')

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
      options.notifyError(error)
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
      options.notifyError(error)
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

  async function loadLlmProfiles(): Promise<void> {
    try {
      llmProfiles.value = await api.listLlmProfiles()
      if (!llmProfiles.value.some(item => item.id === options.selectedLlmProfileId.value)) {
        options.selectedLlmProfileId.value = llmProfiles.value[0]?.id || ''
      }
    } catch (error) {
      options.notifyError(error)
    }
  }

  async function loadMcpProfiles(): Promise<void> {
    try {
      mcpServers.value = await api.listMcpProfiles()
    } catch (error) {
      options.notifyError(error)
    }
  }

  async function loadSkillCatalog(): Promise<void> {
    try {
      skillCatalog.value = await api.listSkills()
    } catch (error) {
      options.notifyError(error)
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
      await ElMessageBox.confirm(`确认删除大模型配置「${profile.name}」吗？删除后执行请求将无法再选择该配置。`, '删除大模型配置', { type: 'warning' })
      await api.deleteLlmProfile(profile.id)
      llmProfiles.value.splice(selectedLlmIndex.value, 1)
      if (options.selectedLlmProfileId.value === profile.id) {
        options.selectedLlmProfileId.value = llmProfiles.value[0]?.id || ''
      }
      selectedLlmIndex.value = llmProfiles.value.length ? 0 : -1
      if (selectedLlmIndex.value >= 0) selectLlm(selectedLlmIndex.value)
      ElMessage.success('大模型配置已删除')
    } catch (error) {
      if (error !== 'cancel' && error !== 'close') options.notifyError(error)
    }
  }

  async function saveLlm(): Promise<void> {
    const profile = llmDraft.value
    const id = profile.id.trim()
    if (!id || !/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(id)) return options.notifyError(new Error('LLM ID 只能使用字母、数字、点、下划线或短横线'))
    if (!profile.name.trim() || !profile.endpoint.trim() || !profile.modelId.trim() || profile.contextWindowTokens <= 0) return options.notifyError(new Error('请填写名称、Endpoint、模型 ID 和有效的上下文大小'))
    profile.id = id
    savingLlm.value = true
    try {
      const saved = await api.saveLlmProfile(id, profile)
      const existingIndex = llmProfiles.value.findIndex(item => item.id === saved.id)
      if (existingIndex >= 0) llmProfiles.value[existingIndex] = saved
      else llmProfiles.value.push(saved)
      if (!options.selectedLlmProfileId.value) options.selectedLlmProfileId.value = saved.id
      selectLlm(existingIndex >= 0 ? existingIndex : llmProfiles.value.length - 1)
      showLlmEditor.value = false
      ElMessage.success('大模型配置已保存')
    } catch (error) {
      options.notifyError(error)
    } finally {
      savingLlm.value = false
    }
  }

  async function testLlm(): Promise<void> {
    testingLlm.value = true
    try {
      llmResult.value = await api.testLlmProfile(llmDraft.value)
    } catch (error) {
      options.notifyError(error)
    } finally {
      testingLlm.value = false
    }
  }

  async function refreshAgents(): Promise<void> {
    refreshingAgents.value = true
    try {
      const refreshed = await api.listAgents()
      options.agents.value = refreshed
      if ((options.connectionMode.value === 'engine' && options.selectedAgentId.value === AUTO_AGENT_ID)
        || (options.selectedAgentId.value !== AUTO_AGENT_ID && !refreshed.some(item => item.agentId === options.selectedAgentId.value))) {
        options.selectedAgentId.value = refreshed[0]?.agentId || ''
        config.value = null
      }
      if (options.selectedAgentId.value && options.selectedAgentId.value !== AUTO_AGENT_ID && activeSettings.value === 'agent') {
        await loadConfig()
      }
      ElMessage.success('Agent 列表已刷新')
    } catch (error) {
      options.notifyError(error)
    } finally {
      refreshingAgents.value = false
    }
  }

  async function loadConfig(agentId = options.selectedAgentId.value): Promise<void> {
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
      options.notifyError(error)
    }
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
      if (error !== 'cancel' && error !== 'close') options.notifyError(error)
    }
  }

  async function editAgent(agentId: string): Promise<void> {
    options.selectedAgentId.value = agentId
    handleAgentChange()
    await loadConfig(agentId)
    isNewAgent.value = false
    showAgentEditor.value = true
  }

  function chooseSkillPackage(): void {
    skillPackageInput.value?.click()
  }

  function currentSkillMarkdown(): string {
    return composeSkillMarkdown(skillEditorName.value, skillEditorDescription.value, skillEditorInstructions.value)
  }

  function openSkillTextEditor(): void {
    editingSkillId.value = ''
    skillEditorMode.value = 'form'
    skillEditorName.value = 'my-skill'
    skillEditorDescription.value = 'Describe what this Skill does'
    skillEditorInstructions.value = '# Instructions\n\n'
    skillMarkdownDraft.value = currentSkillMarkdown()
    showSkillTextEditor.value = true
  }

  function switchSkillEditorMode(): void {
    if (skillEditorMode.value === 'form') {
      skillMarkdownDraft.value = currentSkillMarkdown()
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
      options.notifyError(error)
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
      options.notifyError(error)
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
      if (error !== 'cancel' && error !== 'close') options.notifyError(error)
    }
  }

  async function saveTextSkill(): Promise<void> {
    if (skillEditorMode.value === 'form') skillMarkdownDraft.value = currentSkillMarkdown()
    const frontmatter = parseSkillMarkdown(skillMarkdownDraft.value)
    if (!frontmatter) {
      options.notifyError(new Error('Skill Markdown 必须以 YAML frontmatter 开始，并包含 name 与 description'))
      return
    }
    uploadingSkill.value = true
    try {
      await uploadSkillFile(new File([skillMarkdownDraft.value], `${frontmatter.name}.md`, { type: 'text/markdown' }))
      showSkillTextEditor.value = false
    } catch (error) {
      options.notifyError(error)
    } finally {
      uploadingSkill.value = false
    }
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
    if (!options.selectedAgentId.value || !ragDraft.value.id.trim()) return
    try {
      const saved = await api.saveRag(ragDraft.value.id.trim(), options.selectedAgentId.value, ragDraft.value)
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
    } catch (error) { options.notifyError(error) }
  }

  async function deleteRag(): Promise<void> {
    const current = ragInstances.value[selectedRagIndex.value]
    if (!current || !options.selectedAgentId.value) return
    try {
      await ElMessageBox.confirm(`确认移除 RAG「${current.name || current.id}」吗？`, '移除 RAG', { type: 'warning' })
      await api.deleteRag(current.id, options.selectedAgentId.value)
      ragInstances.value.splice(selectedRagIndex.value, 1)
      selectedRagIndex.value = ragInstances.value.length ? 0 : -1
      if (selectedRagIndex.value >= 0) selectRag(selectedRagIndex.value)
      ElMessage.success('RAG 已移除')
    } catch (error) {
      if (error !== 'cancel' && error !== 'close') options.notifyError(error)
    }
  }

  async function testRag(): Promise<void> {
    testingRag.value = true
    try { ragResult.value = await api.testRag(ragDraft.value) } catch (error) { options.notifyError(error) } finally { testingRag.value = false }
  }

  async function testRagRow(index: number): Promise<void> {
    selectRag(index)
    await testRag()
    showRagEditor.value = true
  }

  async function createAgent(): Promise<void> {
    const agentId = `agent-${randomUuid().slice(0, 8)}`
    options.selectedAgentId.value = agentId
    handleAgentChange()
    config.value = createDefaultAgent(agentId, '')
    isNewAgent.value = true
    mcpServers.value = []
    skillDraft.value = { enabledSkills: [], instances: [] }
    ragInstances.value = []
    showAgentEditor.value = true
  }

  async function saveConfig(): Promise<void> {
    if (!config.value) return
    const agentId = config.value.agentId.trim()
    if (!agentId || !/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(agentId)) {
      options.notifyError(new Error('Agent ID 只能使用字母、数字、点、下划线或短横线'))
      return
    }
    if (!config.value.name.trim()) {
      options.notifyError(new Error('请输入 Agent 名称'))
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
      options.selectedAgentId.value = agentId
      options.agents.value = [
        ...options.agents.value.filter(item => item.agentId !== agentId),
        { tenantId: getTenantId(), agentId, name: saved.name, description: saved.description, status: saved.status, currentVersion: saved.currentVersion },
      ]
      isNewAgent.value = false
      showAgentEditor.value = false
      ElMessage.success('Agent 配置已保存')
    } catch (error) {
      options.notifyError(error)
    } finally {
      savingConfig.value = false
    }
  }

  async function saveMcp(): Promise<void> {
    const name = mcpDraft.value.name.trim()
    if (!name) {
      options.notifyError(new Error('请输入 MCP 名称'))
      return
    }
    if (!mcpDraft.value.url.trim()) {
      options.notifyError(new Error('请输入 MCP URL'))
      return
    }
    const duplicate = mcpServers.value.findIndex((item, index) =>
      index !== selectedMcpIndex.value && item.name.trim().toLowerCase() === name.toLowerCase())
    if (duplicate >= 0) {
      options.notifyError(new Error(`MCP「${name}」已经存在`))
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
      options.notifyError(error)
    }
  }

  async function testMcp(): Promise<void> {
    testingMcp.value = true
    try {
      mcpResult.value = await api.testMcp(mcpDraft.value, config.value?.agentId)
    } catch (error) {
      options.notifyError(error)
    } finally {
      testingMcp.value = false
    }
  }

  function handleAgentChange(): void {
    config.value = null
    agentMcpIds.value = []
    mcpServers.value = []
    mcpBindingOptions.value = []
    selectedMcpIndex.value = -1
    skillBindingOptions.value = []
    skillDraft.value = { enabledSkills: [], instances: [] }
    ragInstances.value = []
    selectedRagIndex.value = -1
  }

  function selectAgent(agentId: string): void {
    if (options.selectedAgentId.value === agentId) return
    options.selectedAgentId.value = agentId
    handleAgentChange()
    void loadConfig()
  }

  function openSettings(panel: SettingsPanel): void {
    activeSettings.value = panel
    showSettings.value = true
    if (panel === 'llm') void loadLlmProfiles()
    if (panel === 'mcp') void loadMcpProfiles()
    if (panel === 'skill') void loadSkillCatalog()
    if (panel === 'agent') {
      void loadConfig()
    }
    if (panel === 'rag') void loadConfig()
  }

  function handleSettingsTabChange(name: string | number): void {
    if (name === 'llm') void loadLlmProfiles()
    if (name === 'mcp') void loadMcpProfiles()
    if (name === 'skill') void loadSkillCatalog()
    if (name === 'agent') {
      void loadConfig()
    }
    if (name === 'rag') void loadConfig()
  }

  function resetSettings(): void {
    config.value = null
    llmDraft.value = createDefaultLlm()
    llmProfiles.value = []
    mcpDraft.value = createDefaultMcp()
    mcpServers.value = []
    skillDraft.value = { enabledSkills: [], instances: [] }
    ragDraft.value = createDefaultRag()
    ragInstances.value = []
    llmResult.value = null
    mcpResult.value = null
    ragResult.value = null
    showAgentEditor.value = false
    showLlmEditor.value = false
    showMcpEditor.value = false
    showRagEditor.value = false
  }

  return {
    showSettings,
    activeSettings,
    savingConfig,
    refreshingAgents,
    testingMcp,
    uploadingSkill,
    testingRag,
    config,
    showAgentEditor,
    isNewAgent,
    showMcpEditor,
    showRagEditor,
    mcpDraft,
    mcpServers,
    agentMcpIds,
    showMcpBindingPicker,
    mcpBindingOptions,
    loadingMcpBindingOptions,
    selectedMcpIndex,
    mcpResult,
    skillPackageInput,
    showSkillTextEditor,
    skillMarkdownDraft,
    skillEditorMode,
    skillEditorName,
    skillEditorDescription,
    skillEditorInstructions,
    editingSkillId,
    showSkillBindingPicker,
    skillBindingOptions,
    loadingSkillBindingOptions,
    skillCatalog,
    skillDraft,
    ragDraft,
    ragInstances,
    selectedRagIndex,
    ragResult,
    llmProfiles,
    llmDraft,
    selectedLlmIndex,
    llmResult,
    testingLlm,
    savingLlm,
    showLlmEditor,
    isNewLlm,
    boundMcpServers,
    boundSkills,
    ragEnabledText,
    isSkillEnabled,
    toggleSkillBinding,
    toggleMcpBinding,
    openMcpBindingPicker,
    openSkillBindingPicker,
    removeMcpBinding,
    removeSkillBinding,
    isRagEnabled,
    toggleRagBinding,
    loadLlmProfiles,
    loadMcpProfiles,
    loadSkillCatalog,
    selectLlm,
    newLlm,
    editLlm,
    deleteLlm,
    saveLlm,
    testLlm,
    refreshAgents,
    loadConfig,
    selectMcp,
    newMcp,
    removeMcp,
    editAgent,
    chooseSkillPackage,
    openSkillTextEditor,
    switchSkillEditorMode,
    editSkill,
    uploadSkillPackage,
    deleteSkillCatalog,
    saveTextSkill,
    selectRag,
    newRag,
    editRag,
    saveRag,
    deleteRag,
    testRag,
    testRagRow,
    createAgent,
    saveConfig,
    saveMcp,
    testMcp,
    handleAgentChange,
    selectAgent,
    openSettings,
    handleSettingsTabChange,
    resetSettings,
  }
}
