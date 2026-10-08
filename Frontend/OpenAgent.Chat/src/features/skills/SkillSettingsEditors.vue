<script setup lang="ts">
import type { useSkillSettings } from './useSkillSettings'

const props = defineProps<{ context: ReturnType<typeof useSkillSettings> }>()
const { uploadingSkill, showSkillTextEditor, skillMarkdownDraft, skillEditorMode, skillEditorName, skillEditorDescription, skillEditorInstructions, editingSkillId, showSkillBindingPicker, skillBindingOptions, isSkillEnabled, toggleSkillBinding, switchSkillEditorMode, saveTextSkill } = props.context
</script>

<template>
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

  <el-dialog v-model="showSkillBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 Skill" width="min(820px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="skillBindingOptions" max-height="440" empty-text="还没有可绑定的 Skill"><el-table-column label="名称" min-width="190"><template #default="scope"><strong>{{ scope.row.name || '未命名 Skill' }}</strong><small class="table-subtext">{{ scope.row.skillId }}</small></template></el-table-column><el-table-column label="说明" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.description }}</template></el-table-column><el-table-column label="资源" width="100"><template #default="scope">{{ scope.row.resourceCount || 0 }}</template></el-table-column><el-table-column label="脚本" width="90"><template #default="scope"><span :class="['table-status', { muted: !(scope.row.scriptCount || 0) }]"><i />{{ scope.row.scriptCount || 0 }}{{ scope.row.scriptExecutionEnabled ? ' · 已启用' : '' }}</span></template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="isSkillEnabled(scope.row.skillId)" @change="toggleSkillBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showSkillBindingPicker = false">完成</el-button></template>
  </el-dialog>
</template>
