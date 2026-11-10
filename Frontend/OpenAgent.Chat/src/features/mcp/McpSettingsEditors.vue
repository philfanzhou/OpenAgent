<script setup lang="ts">
import type { useMcpSettings } from './useMcpSettings'

const props = defineProps<{ context: ReturnType<typeof useMcpSettings> }>()
const { showMcpEditor, mcpDraft, agentMcpIds, showMcpBindingPicker, mcpBindingOptions, selectedMcpIndex, toggleMcpBinding, saveMcp, testMcp, testingMcp, mcpTestResult, mcpTestTarget, clearMcpTest } = props.context
</script>

<template>
  <el-dialog v-model="showMcpEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top">
      <el-form-item label="名称"><el-input v-model="mcpDraft.name" :disabled="selectedMcpIndex >= 0" placeholder="例如 local-tools" /><small class="form-help">名称同时作为 MCP 配置 ID；编辑时不可修改，避免已有 Agent 绑定失效。</small></el-form-item>
      <el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="Streamable HTTP" value="Http" /><el-option label="Legacy SSE" value="SSE" /></el-select></el-form-item>
      <el-form-item label="MCP 协议版本"><el-select v-model="mcpDraft.protocolVersion" clearable filterable allow-create placeholder="自动协商（推荐）"><el-option label="2026-07-28" value="2026-07-28" /><el-option label="2025-11-25" value="2025-11-25" /><el-option label="2025-06-18" value="2025-06-18" /><el-option label="2025-03-26" value="2025-03-26" /><el-option label="2024-11-05" value="2024-11-05" /></el-select><small class="form-help">测试时要求服务端协商为指定版本；留空自动协商。聊天运行链路不使用此 SDK。</small></el-form-item>
      <el-form-item label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com/mcp" /></el-form-item>
    </el-form>
    <template #footer>
      <div class="mcp-editor-actions">
        <el-button @click="showMcpEditor = false; clearMcpTest()">取消</el-button>
        <el-button type="success" :loading="testingMcp" :disabled="!mcpDraft.url.trim() || testingMcp" @click="testMcp(mcpDraft)">测试连接</el-button>
        <el-button type="primary" :disabled="!mcpDraft.name.trim() || !mcpDraft.url.trim()" @click="saveMcp">保存 MCP 配置</el-button>
      </div>
    </template>
  </el-dialog>

  <el-dialog :model-value="Boolean(mcpTestTarget)" class="editor-dialog mcp-diagnostic" modal-class="editor-overlay" title="MCP 连接诊断" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close @close="clearMcpTest">
    <p v-if="mcpTestTarget" class="mcp-test-target">
      <strong>{{ mcpTestTarget.name || '未命名' }}</strong>
      <span>{{ mcpTestTarget.url }}</span>
    </p>
    <el-skeleton v-if="testingMcp" :rows="4" animated aria-label="正在连接 MCP 并读取工具目录" />
    <template v-else-if="mcpTestResult">
      <el-result :icon="mcpTestResult.success ? 'success' : 'error'" :title="mcpTestResult.success ? '连接成功' : (mcpTestResult.connected ? '已连接，工具目录读取失败' : '连接失败')">
        <template #sub-title>{{ mcpTestResult.error || `${mcpTestResult.tools.length} 个工具` }}</template>
      </el-result>
      <div class="mcp-meta">
        <span>耗时：{{ mcpTestResult.latencyMs }} ms</span>
        <span v-if="mcpTestResult.serverName">服务：{{ mcpTestResult.serverName }}</span>
        <span v-if="mcpTestResult.serverVersion">版本：{{ mcpTestResult.serverVersion }}</span>
        <span v-if="mcpTestResult.protocolVersion">协议：{{ mcpTestResult.protocolVersion }}</span>
      </div>
      <el-empty v-if="mcpTestResult.success && !mcpTestResult.tools.length" description="服务器未发布工具" />
      <el-collapse v-else-if="mcpTestResult.tools.length">
        <el-collapse-item v-for="tool in mcpTestResult.tools" :key="tool.name" :title="tool.title ? `${tool.name} · ${tool.title}` : tool.name">
          <p v-if="tool.description" class="mcp-description">{{ tool.description }}</p>
          <h4>输入 Schema</h4>
          <pre class="mcp-schema">{{ JSON.stringify(tool.inputSchema, null, 2) }}</pre>
          <template v-if="tool.outputSchema">
            <h4>输出 Schema</h4>
            <pre class="mcp-schema">{{ JSON.stringify(tool.outputSchema, null, 2) }}</pre>
          </template>
        </el-collapse-item>
      </el-collapse>
    </template>
  </el-dialog>

  <el-dialog v-model="showMcpBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 MCP" width="min(760px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="mcpBindingOptions" max-height="440" empty-text="还没有可绑定的 MCP"><el-table-column label="名称" min-width="170"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.url }}</template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="agentMcpIds.some(id => id.toLowerCase() === scope.row.name.toLowerCase())" @change="toggleMcpBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showMcpBindingPicker = false">完成</el-button></template>
  </el-dialog>
</template>

<style scoped>
.mcp-test-target { display: flex; flex-direction: column; gap: 4px; overflow-wrap: anywhere; }
.mcp-test-target span, .mcp-meta { color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.6; }
.mcp-meta { display: flex; flex-wrap: wrap; gap: 4px 12px; padding-bottom: 12px; overflow-wrap: anywhere; }
.mcp-description { white-space: pre-wrap; overflow-wrap: anywhere; }
.mcp-schema { max-width: 100%; max-height: 260px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; }
:global(.mcp-diagnostic .el-result) { padding: 20px 0; }
:global(.mcp-diagnostic .el-collapse-item__header) { height: auto; min-height: 48px; line-height: 1.5; }
:global(.mcp-diagnostic .el-collapse-item__title) { min-width: 0; overflow-wrap: anywhere; }
.mcp-editor-actions { display: flex; justify-content: flex-end; flex-wrap: wrap; gap: 8px; }
.mcp-editor-actions :deep(.el-button) { margin-left: 0; }
</style>
