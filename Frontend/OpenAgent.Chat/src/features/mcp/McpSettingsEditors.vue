<script setup lang="ts">
import type { useMcpSettings } from './useMcpSettings'

const props = defineProps<{ context: ReturnType<typeof useMcpSettings> }>()
const { testingMcp, showMcpEditor, mcpDraft, agentMcpIds, showMcpBindingPicker, mcpBindingOptions, selectedMcpIndex, mcpResult, toggleMcpBinding, saveMcp, testMcp } = props.context
</script>

<template>
  <el-dialog v-model="showMcpEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top">
      <el-form-item label="名称"><el-input v-model="mcpDraft.name" :disabled="selectedMcpIndex >= 0" placeholder="例如 local-tools" /><small class="form-help">名称同时作为 MCP 配置 ID；编辑时不可修改，避免已有 Agent 绑定失效。</small></el-form-item>
      <el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="Streamable HTTP" value="Http" /><el-option label="Legacy SSE" value="SSE" /></el-select></el-form-item>
      <el-form-item label="MCP 协议版本"><el-select v-model="mcpDraft.protocolVersion" clearable filterable allow-create placeholder="自动协商（推荐）"><el-option label="2026-07-28" value="2026-07-28" /><el-option label="2025-11-25" value="2025-11-25" /><el-option label="2025-06-18" value="2025-06-18" /><el-option label="2025-03-26" value="2025-03-26" /><el-option label="2024-11-05" value="2024-11-05" /></el-select><small class="form-help">留空由官方 SDK 自动协商；指定版本作为最低版本，服务器降级到更早版本时连接会失败。</small></el-form-item>
      <el-form-item label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com/mcp" /></el-form-item>
      <el-alert v-if="mcpResult" :title="`测试结果：${mcpResult.success ? '连接成功' : '连接失败'} · 权限${mcpResult.authorized ? '通过' : '拒绝'}`" :description="mcpResult.error || `协商版本 ${mcpResult.negotiatedProtocolVersion || '未知'} · ${mcpResult.latencyMs}ms · ${mcpResult.toolCount} 个工具`" :type="mcpResult.success ? 'success' : 'warning'" :closable="false" />
    </el-form><template #footer><el-button @click="showMcpEditor = false">取消</el-button><el-button :loading="testingMcp" @click="testMcp">测试连接、版本与权限</el-button><el-button type="primary" :disabled="!mcpDraft.name || !mcpDraft.url" @click="saveMcp">保存 MCP 配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 MCP" width="min(760px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="mcpBindingOptions" max-height="440" empty-text="还没有可绑定的 MCP"><el-table-column label="名称" min-width="170"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.url }}</template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="agentMcpIds.some(id => id.toLowerCase() === scope.row.name.toLowerCase())" @change="toggleMcpBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showMcpBindingPicker = false">完成</el-button></template>
  </el-dialog>
</template>
