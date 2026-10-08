<script setup lang="ts">
import type { useMcpSettings } from './useMcpSettings'

const props = defineProps<{ context: ReturnType<typeof useMcpSettings> }>()
const { showMcpEditor, mcpServers, loadMcpProfiles, selectMcp, newMcp, removeMcp, testMcp } = props.context
</script>

<template>
<section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MCP CATALOG</span><h3>MCP 配置</h3><p>独立维护远程 MCP Server；Agent 页面只选择已注册的 Server，不在 Agent 中复制 Endpoint。</p></div><div class="section-actions"><el-button @click="loadMcpProfiles">刷新</el-button><el-button type="primary" plain @click="newMcp">新增 MCP</el-button></div></div>
            <el-table :data="mcpServers" class="capability-table" empty-text="还没有 MCP 配置"><el-table-column label="名称" min-width="160"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.url || '未配置' }}</template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="selectMcp(scope.$index); showMcpEditor = true">编辑</el-button><el-button link @click="selectMcp(scope.$index); testMcp(); showMcpEditor = true">测试</el-button><el-button link type="danger" @click="removeMcp(scope.$index)">删除</el-button></template></el-table-column></el-table>
          </section>
</template>
