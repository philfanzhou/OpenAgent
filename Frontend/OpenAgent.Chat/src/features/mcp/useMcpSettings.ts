import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref } from 'vue'
import type { McpServerConfig, McpTestResult } from '../../shared/contracts/types'
import { api } from './api'

export function createDefaultMcp(): McpServerConfig {
  return { name: '', url: '', type: 'Http', protocolVersion: null }
}

interface SettingsOptions {
  agentId: () => string | undefined
  notifyError: (error: unknown) => void
}

export function useMcpSettings(options: SettingsOptions) {
  const testingMcp = ref(false)

  const showMcpEditor = ref(false)

  const mcpDraft = ref<McpServerConfig>(createDefaultMcp())

  const mcpServers = ref<McpServerConfig[]>([])

  const agentMcpIds = ref<string[]>([])

  const showMcpBindingPicker = ref(false)

  const mcpBindingOptions = ref<McpServerConfig[]>([])

  const loadingMcpBindingOptions = ref(false)

  const selectedMcpIndex = ref(-1)

  const mcpResult = ref<McpTestResult | null>(null)

  const boundMcpServers = computed(() => agentMcpIds.value.map(id =>
    mcpServers.value.find(item => item.name.toLowerCase() === id.toLowerCase())
    || mcpBindingOptions.value.find(item => item.name.toLowerCase() === id.toLowerCase())
    || { name: id, url: '', type: 'Http' as const }))

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

  function removeMcpBinding(name: string): void {
    agentMcpIds.value = agentMcpIds.value.filter(id => id.toLowerCase() !== name.toLowerCase())
  }

  async function loadMcpProfiles(): Promise<void> {
    try {
      mcpServers.value = await api.listMcpProfiles()
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
      mcpResult.value = await api.testMcp(mcpDraft.value, options.agentId())
    } catch (error) {
      options.notifyError(error)
    } finally {
      testingMcp.value = false
    }
  }

  return { testingMcp, showMcpEditor, mcpDraft, mcpServers, agentMcpIds, showMcpBindingPicker, mcpBindingOptions, loadingMcpBindingOptions, selectedMcpIndex, mcpResult, boundMcpServers, toggleMcpBinding, openMcpBindingPicker, removeMcpBinding, loadMcpProfiles, selectMcp, newMcp, removeMcp, saveMcp, testMcp }
}
