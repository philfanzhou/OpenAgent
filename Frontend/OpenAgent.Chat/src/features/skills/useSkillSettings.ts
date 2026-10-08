import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref } from 'vue'
import type { SkillCatalogItem, SkillInstanceConfig, SkillsConfig } from '../../shared/contracts/types'
import { api } from './api'
import { composeSkillMarkdown, parseSkillMarkdown } from './markdown'

interface SettingsOptions {

  notifyError: (error: unknown) => void
}

export function useSkillSettings(options: SettingsOptions) {
  const uploadingSkill = ref(false)

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

  const enabledSkillIds = computed(() => new Set(skillDraft.value.enabledSkills))

  const boundSkills = computed(() => skillDraft.value.enabledSkills.map(id =>
    skillCatalog.value.find(item => item.skillId.toLowerCase() === id.toLowerCase())
    || skillBindingOptions.value.find(item => item.skillId.toLowerCase() === id.toLowerCase())
    || skillDraft.value.instances.find(item => item.skillId.toLowerCase() === id.toLowerCase())
    || { skillId: id, name: id, enabled: true }))

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

  function removeSkillBinding(skillId: string): void {
    skillDraft.value.enabledSkills = skillDraft.value.enabledSkills.filter(id => id.toLowerCase() !== skillId.toLowerCase())
  }

  async function loadSkillCatalog(): Promise<void> {
    try {
      skillCatalog.value = await api.listSkills()
    } catch (error) {
      options.notifyError(error)
    }
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
    if (installed.skill.scriptCount) {
      ElMessage.success(`Skill 已保存；检测到 ${installed.skill.scriptCount} 个脚本，脚本执行默认关闭，请在列表中审阅后开启`)
    } else {
      ElMessage.success('Skill 已校验并写入 OSS 解压目录；请在 Agent 中选择绑定')
    }
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

  async function toggleSkillScriptExecution(skill: SkillCatalogItem): Promise<void> {
    const enabling = !(skill.scriptExecutionEnabled ?? false)
    const scriptCount = skill.scriptCount ?? skill.scriptNames?.length ?? 0
    if (enabling) {
      if (scriptCount === 0) {
        ElMessage.info('该 Skill 包内没有可执行的脚本，无法启用脚本执行')
        return
      }
      try {
        await ElMessageBox.confirm(
          `启用后，绑定的 Agent 可通过 run_skill_script 在隔离沙箱中运行该 Skill 包内的 ${scriptCount} 个脚本：${(skill.scriptNames ?? []).join('、')}。请确认你信任该 Skill 的来源。`,
          '启用脚本执行',
          { type: 'warning', confirmButtonText: '启用', cancelButtonText: '取消' },
        )
      } catch {
        return
      }
    }
    try {
      const updated = await api.updateSkillScriptExecution(skill.skillId, enabling)
      skillCatalog.value = skillCatalog.value.map(item =>
        item.skillId.toLowerCase() === updated.skillId.toLowerCase() ? updated : item)
      ElMessage.success(enabling ? '已启用该 Skill 的脚本执行' : '已停用该 Skill 的脚本执行')
    } catch (error) {
      options.notifyError(error)
      void loadSkillCatalog()
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
      if (editingSkillId.value) {
        // 编辑已有 Skill 时原地替换 SKILL.md：包内脚本与脚本执行开关保持不变。
        const updated = await api.updateSkillSource(editingSkillId.value, skillMarkdownDraft.value)
        skillCatalog.value = skillCatalog.value.map(item =>
          item.skillId.toLowerCase() === updated.skillId.toLowerCase() ? updated : item)
        ElMessage.success('Skill 内容已更新，包内脚本保持不变')
      } else {
        await uploadSkillFile(new File([skillMarkdownDraft.value], `${frontmatter.name}.md`, { type: 'text/markdown' }))
      }
      showSkillTextEditor.value = false
    } catch (error) {
      options.notifyError(error)
    } finally {
      uploadingSkill.value = false
    }
  }

  return { uploadingSkill, skillPackageInput, showSkillTextEditor, skillMarkdownDraft, skillEditorMode, skillEditorName, skillEditorDescription, skillEditorInstructions, editingSkillId, showSkillBindingPicker, skillBindingOptions, loadingSkillBindingOptions, skillCatalog, skillDraft, boundSkills, isSkillEnabled, toggleSkillBinding, openSkillBindingPicker, removeSkillBinding, loadSkillCatalog, toggleSkillScriptExecution, chooseSkillPackage, openSkillTextEditor, switchSkillEditorMode, editSkill, uploadSkillPackage, deleteSkillCatalog, saveTextSkill }
}
