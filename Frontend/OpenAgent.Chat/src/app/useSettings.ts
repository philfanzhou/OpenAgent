import { ElMessage } from 'element-plus'
import { ref, type Ref } from 'vue'
import { api } from '../features/agents/api'
import { api as mcpApi } from '../features/mcp/api'
import { createDefaultMcp, useMcpSettings } from '../features/mcp/useMcpSettings'
import { createDefaultLlm, useModelSettings } from '../features/models/useModelSettings'
import { createDefaultRag, useRagSettings } from '../features/rag/useRagSettings'
import { api as skillApi } from '../features/skills/api'
import { useSkillSettings } from '../features/skills/useSkillSettings'
import { getTenantId } from '../shared/api/http'
import { randomUuid } from '../shared/browserCrypto'
import type { AgentConfigEntity, AgentSummary, ConnectionMode, McpServerConfig, SkillCatalogItem } from '../shared/contracts/types'
import { AUTO_AGENT_ID } from '../shared/contracts/types'

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
      codeExecution: { enabled: false },
      maxTurns: 50,
    },
  }
}

export function useSettings(options: SettingsOptions) {
  const showSettings = ref(!(options.connectionMode.value === 'router' ? options.routerUrl.value : options.engineUrl.value))

  const activeSettings = ref<SettingsPanel>('gateway')

  const savingConfig = ref(false)

  const refreshingAgents = ref(false)

  const refreshingCatalog = ref(false)

  const config = ref<AgentConfigEntity | null>(null)

  const showAgentEditor = ref(false)

  const isNewAgent = ref(false)

  const { llmProfiles, llmDraft, selectedLlmIndex, llmResult, testingLlm, savingLlm, showLlmEditor, isNewLlm, loadLlmProfiles, selectLlm, newLlm, editLlm, deleteLlm, saveLlm, testLlm } = useModelSettings({ selectedLlmProfileId: options.selectedLlmProfileId, notifyError: options.notifyError })
  const { testingMcp, showMcpEditor, mcpDraft, mcpServers, agentMcpIds, showMcpBindingPicker, mcpBindingOptions, loadingMcpBindingOptions, selectedMcpIndex, mcpResult, boundMcpServers, toggleMcpBinding, openMcpBindingPicker, removeMcpBinding, loadMcpProfiles, selectMcp, newMcp, removeMcp, saveMcp, testMcp } = useMcpSettings({ agentId: () => config.value?.agentId, notifyError: options.notifyError })
  const { uploadingSkill, skillPackageInput, showSkillTextEditor, skillMarkdownDraft, skillEditorMode, skillEditorName, skillEditorDescription, skillEditorInstructions, editingSkillId, showSkillBindingPicker, skillBindingOptions, loadingSkillBindingOptions, skillCatalog, skillDraft, boundSkills, isSkillEnabled, toggleSkillBinding, openSkillBindingPicker, removeSkillBinding, loadSkillCatalog, toggleSkillScriptExecution, chooseSkillPackage, openSkillTextEditor, switchSkillEditorMode, editSkill, uploadSkillPackage, deleteSkillCatalog, saveTextSkill } = useSkillSettings({ notifyError: options.notifyError })
  const { testingRag, showRagEditor, ragDraft, ragInstances, selectedRagIndex, ragResult, ragEnabledText, isRagEnabled, toggleRagBinding, selectRag, newRag, editRag, saveRag, deleteRag, testRag, testRagRow } = useRagSettings({ selectedAgentId: options.selectedAgentId, config, notifyError: options.notifyError })

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

  async function refreshAgents(showSuccess = true): Promise<void> {
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
      if (showSuccess) ElMessage.success('Agent 列表已刷新')
    } catch (error) {
      options.notifyError(error)
    } finally {
      refreshingAgents.value = false
    }
  }

  async function refreshCatalog(): Promise<void> {
    refreshingCatalog.value = true
    try {
      await Promise.all([refreshAgents(false), loadLlmProfiles()])
      ElMessage.success('Agent 和模型列表已刷新')
    } finally {
      refreshingCatalog.value = false
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
        Promise.all(mcpIds.map(id => mcpApi.getMcpProfile(id).catch(() => null))),
        Promise.all(loadedConfig.config.skills.enabledSkills.map(id => skillApi.getSkill(id).catch(() => null))),
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

  async function editAgent(agentId: string): Promise<void> {
    options.selectedAgentId.value = agentId
    handleAgentChange()
    await loadConfig(agentId)
    isNewAgent.value = false
    showAgentEditor.value = true
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
    refreshingCatalog,
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
    toggleSkillScriptExecution,
    selectLlm,
    newLlm,
    editLlm,
    deleteLlm,
    saveLlm,
    testLlm,
    refreshAgents,
    refreshCatalog,
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
