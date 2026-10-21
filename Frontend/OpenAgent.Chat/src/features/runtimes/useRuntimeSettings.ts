import { ref, type Ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { RuntimeProfile } from '../../shared/contracts/types'
import { runtimeApi } from './api'

const fresh = (): RuntimeProfile => ({ id: '', name: '', provider: 'OpenCode', turnTimeoutSeconds: 1800 })

export function useRuntimeSettings(selected: Ref<string>, notifyError: (error: unknown) => void) {
  const profiles = ref<RuntimeProfile[]>([])
  const draft = ref<RuntimeProfile>(fresh())
  const editing = ref(false)
  const saving = ref(false)
  const creating = ref(true)
  async function load(): Promise<void> {
    try {
      profiles.value = await runtimeApi.list()
      if (!profiles.value.some(profile => profile.id === selected.value)) selected.value = profiles.value[0]?.id || ''
    } catch (error) { notifyError(error) }
  }
  function create(): void { draft.value = fresh(); creating.value = true; editing.value = true }
  function edit(profile: RuntimeProfile): void { draft.value = { ...profile }; creating.value = false; editing.value = true }
  async function save(): Promise<void> {
    if (!draft.value.id.trim() || !draft.value.name.trim()) { ElMessage.error('请填写 Runtime ID 和名称'); return }
    saving.value = true
    try {
      await runtimeApi.save(draft.value)
      selected.value = draft.value.id
      await load()
      editing.value = false
      ElMessage.success('Runtime 已保存')
    } catch (error) { notifyError(error) } finally { saving.value = false }
  }
  async function remove(profile: RuntimeProfile): Promise<void> {
    try {
      await ElMessageBox.confirm(`删除 Runtime“${profile.name}”？`, '删除 Runtime', { type: 'warning' })
      await runtimeApi.remove(profile.id)
      await load()
    } catch (error) { if (error !== 'cancel' && error !== 'close') notifyError(error) }
  }
  function reset(): void { profiles.value = []; draft.value = fresh(); editing.value = false; creating.value = true }
  return { profiles, draft, editing, saving, creating, load, create, edit, save, remove, reset }
}
