<script setup lang="ts">
import type { useRuntimeSettings } from './useRuntimeSettings'
const props = defineProps<{ context: ReturnType<typeof useRuntimeSettings> }>()
const { profiles, draft, editing, saving, creating, load, create, edit, save, remove } = props.context
</script>

<template>
  <section class="settings-section">
    <div class="section-heading"><div><span class="eyebrow">RUNTIMES</span><h3>Runtime 设置</h3><p>独立保存运行时，在会话上方选择。模型连接、Skill 和 MCP 继续使用原有设置与绑定。</p></div><div class="section-actions"><el-button @click="load">刷新</el-button><el-button type="primary" @click="create">新增 Runtime</el-button></div></div>
    <el-table :data="profiles" empty-text="还没有 Runtime，请先新增">
      <el-table-column prop="name" label="名称" /><el-table-column prop="id" label="ID" />
      <el-table-column prop="provider" label="运行时" /><el-table-column prop="turnTimeoutSeconds" label="单轮时限（秒）" />
      <el-table-column label="操作"><template #default="{ row }"><el-button link @click="edit(row)">编辑</el-button><el-button link type="danger" @click="remove(row)">删除</el-button></template></el-table-column>
    </el-table>
  </section>
  <el-dialog v-model="editing" :title="creating ? '新增 Runtime' : '编辑 Runtime'" width="560px" append-to-body :close-on-click-modal="false">
    <el-form label-position="top">
      <el-form-item label="Runtime ID"><el-input v-model="draft.id" :disabled="!creating" placeholder="例如 local-opencode" /></el-form-item>
      <el-form-item label="名称"><el-input v-model="draft.name" placeholder="例如 本机 OpenCode" /></el-form-item>
      <el-form-item label="运行时类型"><el-select v-model="draft.provider" style="width:100%"><el-option label="OpenCode" value="OpenCode" /><el-option label="Claude Code（暂未开放）" value="ClaudeCode" disabled /><el-option label="Codex CLI（暂未开放）" value="Codex" disabled /><el-option label="Gemini CLI（暂未开放）" value="Gemini" disabled /></el-select></el-form-item>
      <el-form-item label="单轮时限（秒）"><el-input-number v-model="draft.turnTimeoutSeconds" :min="1" :max="86400" /><small class="form-help">实际时限同时受服务器上限约束。</small></el-form-item>
    </el-form>
    <template #footer><el-button @click="editing = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存 Runtime</el-button></template>
  </el-dialog>
</template>
