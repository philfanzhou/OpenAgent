<script setup lang="ts">
import type { useModelSettings } from './useModelSettings'

const props = defineProps<{ context: ReturnType<typeof useModelSettings> }>()
const { llmProfiles, showLlmEditor, loadLlmProfiles, selectLlm, newLlm, editLlm, deleteLlm, testLlm } = props.context
</script>

<template>
<section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MODEL PROFILES</span><h3>大模型配置</h3><p>按租户维护模型、上下文大小、协议、Endpoint 和密钥；执行时独立选择，不与 Agent 强制绑定。</p></div><div class="section-actions"><el-button @click="loadLlmProfiles">刷新</el-button><el-button type="primary" plain @click="newLlm">新增配置</el-button></div></div>
            <el-table :data="llmProfiles" class="capability-table" empty-text="还没有大模型配置"><el-table-column label="名称" min-width="140"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="模型" min-width="150"><template #default="scope">{{ scope.row.modelId }}</template></el-table-column><el-table-column label="能力" width="110"><template #default="scope"><el-tag size="small" round>{{ scope.row.modality === 'Multimodal' ? '多模态' : '文本' }}</el-tag></template></el-table-column><el-table-column label="上下文" width="120"><template #default="scope">{{ scope.row.contextTokens.toLocaleString() }}</template></el-table-column><el-table-column label="协议" width="160"><template #default="scope"><el-tag size="small" round>{{ scope.row.format }}</el-tag></template></el-table-column><el-table-column label="Endpoint" min-width="200" show-overflow-tooltip><template #default="scope">{{ scope.row.endpoint }}</template></el-table-column><el-table-column label="密钥" width="100"><template #default>已保护</template></el-table-column><el-table-column label="操作" width="160" fixed="right"><template #default="scope"><el-button link type="primary" @click="editLlm(scope.$index)">编辑</el-button><el-button link @click="selectLlm(scope.$index); testLlm(); showLlmEditor = true">测试</el-button><el-button link type="danger" @click="selectLlm(scope.$index); deleteLlm()">删除</el-button></template></el-table-column></el-table>
          </section>
</template>
