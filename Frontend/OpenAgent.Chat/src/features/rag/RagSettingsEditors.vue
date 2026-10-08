<script setup lang="ts">
import type { useRagSettings } from './useRagSettings'

const props = defineProps<{ context: ReturnType<typeof useRagSettings> }>()
const { testingRag, showRagEditor, ragDraft, ragResult, saveRag, testRag } = props.context
</script>

<template>
  <el-dialog v-model="showRagEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 RAG" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="RAG ID"><el-input v-model="ragDraft.id" placeholder="例如 knowledge-base" /></el-form-item><el-form-item label="名称"><el-input v-model="ragDraft.name" placeholder="例如 企业知识库" /></el-form-item><el-form-item label="类型"><el-select v-model="ragDraft.type"><el-option label="RAGFlow" value="ragflow" /><el-option label="Qdrant" value="qdrant" /></el-select></el-form-item><el-form-item label="Endpoint"><el-input v-model="ragDraft.apiEndpoint" placeholder="https://rag.example.com/api/search" /></el-form-item><el-form-item label="Collection / Dataset"><el-input v-model="ragDraft.collectionName" /></el-form-item><el-form-item label="API Key"><el-input v-model="ragDraft.apiKey" placeholder="编辑时留空表示保留现有密钥" /></el-form-item><el-form-item label="状态"><el-switch v-model="ragDraft.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="ragResult" :title="`测试结果：${ragResult.success ? '连接成功' : '连接失败'}`" :description="ragResult.error || `HTTP ${ragResult.statusCode || '-'} · 延迟 ${ragResult.latencyMs}ms`" :type="ragResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showRagEditor = false">取消</el-button><el-button :loading="testingRag" @click="testRag">测试连接</el-button><el-button type="primary" :disabled="!ragDraft.id" @click="saveRag">保存 RAG 配置</el-button></template>
  </el-dialog>
</template>
