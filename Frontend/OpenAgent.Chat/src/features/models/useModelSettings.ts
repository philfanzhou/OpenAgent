import { ElMessage, ElMessageBox } from 'element-plus'
import { ref, type Ref } from 'vue'
import type { LlmProviderProfile, LlmTestResult } from '../../shared/contracts/types'
import { api } from './api'

export function createDefaultLlm(): LlmProviderProfile {
  return {
    id: '',
    name: '',
    format: 'OpenAIChatCompletions',
    modelId: 'gpt-4o',
    contextTokens: 128000,
    endpoint: 'https://api.openai.com/v1',
    apiKey: '',
    temperature: 0.7,
    modality: 'Text',
  }
}

interface SettingsOptions {
  selectedLlmProfileId: Ref<string>
  notifyError: (error: unknown) => void
}

export function useModelSettings(options: SettingsOptions) {
  const llmProfiles = ref<LlmProviderProfile[]>([])

  const llmDraft = ref<LlmProviderProfile>(createDefaultLlm())

  const selectedLlmIndex = ref(-1)

  const llmResult = ref<LlmTestResult | null>(null)

  const testingLlm = ref(false)

  const savingLlm = ref(false)

  const showLlmEditor = ref(false)

  const isNewLlm = ref(false)

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
    if (!profile.name.trim() || !profile.endpoint.trim() || !profile.modelId.trim() || profile.contextTokens <= 0) return options.notifyError(new Error('请填写名称、Endpoint、模型 ID 和有效的上下文大小'))
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

  return { llmProfiles, llmDraft, selectedLlmIndex, llmResult, testingLlm, savingLlm, showLlmEditor, isNewLlm, loadLlmProfiles, selectLlm, newLlm, editLlm, deleteLlm, saveLlm, testLlm }
}
