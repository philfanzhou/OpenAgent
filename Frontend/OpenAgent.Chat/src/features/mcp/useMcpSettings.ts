import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref } from 'vue'
import type { McpConnectionTestResult, McpServerConfig } from '../../shared/contracts/types'
import { api } from './api'

export function createDefaultMcp(): McpServerConfig {
  return { name: '', url: '', type: 'Http', protocolVersion: null }
}

interface SettingsOptions {
  agentId: () => string | undefined
  notifyError: (error: unknown) => void
}

export function useMcpSettings(options: SettingsOptions) {
  const showMcpEditor = ref(false)

  const mcpDraft = ref<McpServerConfig>(createDefaultMcp())

  const mcpServers = ref<McpServerConfig[]>([])

  const agentMcpIds = ref<string[]>([])

  const showMcpBindingPicker = ref(false)

  const mcpBindingOptions = ref<McpServerConfig[]>([])

  const loadingMcpBindingOptions = ref(false)

  const selectedMcpIndex = ref(-1)
  const testingMcp = ref(false)
  const mcpTestResult = ref<McpConnectionTestResult | null>(null)
  const mcpTestTarget = ref<McpServerConfig | null>(null)
  let mcpTestSequence = 0

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
    showMcpEditor.value = true
  }
  function clearMcpTest(): void {
    mcpTestSequence += 1
    testingMcp.value = false
    mcpTestResult.value = null
    mcpTestTarget.value = null
  }

  async function testMcp(server: McpServerConfig): Promise<void> {
    const target = { ...server, name: server.name.trim(), url: server.url.trim() }
    if (!target.url) return
    const sequence = ++mcpTestSequence
    testingMcp.value = true
    mcpTestResult.value = null
    mcpTestTarget.value = target
    try {
      const result = await api.testConnection(target)
      if (sequence === mcpTestSequence) mcpTestResult.value = result
    } catch (error) {
      if (sequence === mcpTestSequence) {
        mcpTestResult.value = { success: false, connected: false, latencyMs: 0, serverName: null, serverVersion: null, protocolVersion: null, tools: [], error: error instanceof Error ? error.message : '连接测试失败' }
      }
    } finally {
      if (sequence === mcpTestSequence) testingMcp.value = false
    }
  }

  function testMcpRow(index: number): Promise<void> {
    const server = mcpServers.value[index]
    return server ? testMcp(server) : Promise.resolve()
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

  return { showMcpEditor, mcpDraft, mcpServers, agentMcpIds, showMcpBindingPicker, mcpBindingOptions, loadingMcpBindingOptions, selectedMcpIndex, boundMcpServers, testingMcp, mcpTestResult, mcpTestTarget, clearMcpTest, testMcp, testMcpRow, toggleMcpBinding, openMcpBindingPicker, removeMcpBinding, loadMcpProfiles, selectMcp, newMcp, removeMcp, saveMcp }
}
