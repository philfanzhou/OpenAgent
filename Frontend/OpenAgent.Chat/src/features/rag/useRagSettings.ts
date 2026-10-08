import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref, type Ref } from 'vue'
import type { AgentConfigEntity, RagInstanceConfig, RagTestResult } from '../../shared/contracts/types'
import { api } from './api'

export function createDefaultRag(): RagInstanceConfig {
  return { id: '', name: '', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: '', apiKeySecretRef: '', apiKey: '' }
}

interface SettingsOptions {
  selectedAgentId: Ref<string>
  config: Ref<AgentConfigEntity | null>
  notifyError: (error: unknown) => void
}

export function useRagSettings(options: SettingsOptions) {
  const { config } = options

  const testingRag = ref(false)

  const showRagEditor = ref(false)

  const ragDraft = ref<RagInstanceConfig>(createDefaultRag())

  const ragInstances = ref<RagInstanceConfig[]>([])

  const selectedRagIndex = ref(-1)

  const ragResult = ref<RagTestResult | null>(null)

  const enabledRagIds = computed(() => new Set(config.value?.config.rag?.enabledRagInstanceIds || ragInstances.value.filter(item => item.enabled).map(item => item.id)))

  const ragEnabledText = computed(() => config.value?.config.rag?.enabled ? '已启用' : '未启用')

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

  return { testingRag, showRagEditor, ragDraft, ragInstances, selectedRagIndex, ragResult, ragEnabledText, isRagEnabled, toggleRagBinding, selectRag, newRag, editRag, saveRag, deleteRag, testRag, testRagRow }
}
