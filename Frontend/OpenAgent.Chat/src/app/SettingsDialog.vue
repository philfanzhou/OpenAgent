<script setup lang="ts">
import ModelSettingsPanel from '../features/models/ModelSettingsPanel.vue'
import ModelSettingsEditors from '../features/models/ModelSettingsEditors.vue'
import McpSettingsPanel from '../features/mcp/McpSettingsPanel.vue'
import McpSettingsEditors from '../features/mcp/McpSettingsEditors.vue'
import SkillSettingsPanel from '../features/skills/SkillSettingsPanel.vue'
import SkillSettingsEditors from '../features/skills/SkillSettingsEditors.vue'
import RagSettingsPanel from '../features/rag/RagSettingsPanel.vue'
import RagSettingsEditors from '../features/rag/RagSettingsEditors.vue'
import HealthCheckPanel from '../components/HealthCheckPanel.vue'
import type { SettingsDialogContext } from './settings.types'

const props = defineProps<{ context: SettingsDialogContext }>()
const { connectionMode, routerUrl, engineUrl, tenantId, statusText, authConfig, currentUser, agents, activeEndpointLabel, activeEndpointHost, connect, logout, testHealth, showSettings, activeSettings, savingConfig, refreshingAgents, config, showAgentEditor, isNewAgent, loadingMcpBindingOptions, loadingSkillBindingOptions, ragInstances, boundMcpServers, boundSkills, openMcpBindingPicker, openSkillBindingPicker, removeMcpBinding, removeSkillBinding, isRagEnabled, toggleRagBinding, refreshAgents, editAgent, createAgent, saveConfig, openSettings, handleSettingsTabChange } = props.context
</script>

<template>
  <el-dialog v-model="showSettings" class="settings-dialog" modal-class="settings-overlay" width="min(1180px, calc(100vw - 40px))" top="3vh" :close-on-click-modal="false" destroy-on-close>
    <template #header>
      <div class="settings-header"><div><span class="eyebrow">OPENAGENT CONTROL PLANE</span><h2>工作台设置</h2></div><span class="settings-endpoint">{{ activeEndpointLabel }} · {{ activeEndpointHost }}</span></div>
</template>
    <div class="settings-body">
      <el-tabs v-model="activeSettings" tab-position="left" class="settings-tabs" @tab-change="handleSettingsTabChange">
        <el-tab-pane label="连接" name="gateway">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CONNECTION</span><h3>服务连接与身份</h3><p>Router 模式提供意图路由、外部 Agent 和 Engine 服务发现；Engine 模式用于直接联调单个 Engine。</p></div><span class="connection-badge" :class="{ online: statusText === '已连接' }"><i />{{ statusText }}</span></div>
            <el-form label-position="top" class="connection-form"><el-form-item label="连接模式"><el-radio-group v-model="connectionMode"><el-radio-button value="router">Router</el-radio-button><el-radio-button value="engine">直连 Engine</el-radio-button></el-radio-group></el-form-item><el-form-item label="Router 地址"><el-input v-model="routerUrl" placeholder="http://localhost:5001" /></el-form-item><el-form-item label="Engine 地址"><el-input v-model="engineUrl" placeholder="http://localhost:5000" /></el-form-item><el-form-item label="租户 ID"><el-input v-model="tenantId" placeholder="可选：用于当前工作台隔离" /></el-form-item></el-form>
            <el-descriptions :column="2" border class="identity-status"><el-descriptions-item label="当前用户">{{ currentUser?.userId || '未连接' }}</el-descriptions-item><el-descriptions-item label="当前租户">{{ currentUser?.tenantId || tenantId || '未识别' }}</el-descriptions-item><el-descriptions-item label="认证状态">{{ currentUser?.isAuthenticated ? '已认证' : '未认证' }}</el-descriptions-item><el-descriptions-item label="认证模式">{{ authConfig?.mode || '未知' }}</el-descriptions-item></el-descriptions>
            <el-alert v-if="authConfig?.mode === 'Basic'" title="当前 Basic 模式仅用于 Development 联调，不校验真实密码，严禁用于生产环境。" type="warning" :closable="false" />
            <el-alert v-else title="身份认证由企业 IdP 完成；角色、Agent ACL 与租户授权继续由服务端策略独立判定。" type="info" :closable="false" />
            <div class="button-row"><el-button type="danger" plain @click="logout">退出登录并清理会话</el-button></div>
            <div class="button-row"><el-button type="primary" @click="connect">保存并连接</el-button><el-button @click="testHealth('/health')">测试 Live</el-button><el-button @click="testHealth('/ready')">测试 Ready</el-button></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="健康检查" name="health">
          <section class="settings-section">
            <HealthCheckPanel />
          </section>
        </el-tab-pane>
        <el-tab-pane label="LLM 配置" name="llm">
          <ModelSettingsPanel :context="props.context" />
        </el-tab-pane>
        <el-tab-pane label="MCP 配置" name="mcp">
          <McpSettingsPanel :context="props.context" />
        </el-tab-pane>
        <el-tab-pane label="Skill 配置" name="skill">
          <SkillSettingsPanel :context="props.context" />
        </el-tab-pane>
        <el-tab-pane label="Agent 配置" name="agent">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">AGENT RUNTIME</span><h3>Agent 配置</h3><p>Agent 只维护指令、运行边界和能力绑定；模型在每次执行时独立选择。</p></div><div class="section-actions"><el-button @click="refreshAgents" :loading="refreshingAgents">刷新 Agent</el-button><el-button type="primary" plain @click="createAgent">新增 Agent</el-button></div></div>
            <div class="agent-card-grid"><article v-for="agent in agents" :key="agent.agentId" class="agent-card"><h4>{{ agent.name || agent.agentId }}</h4><p>{{ agent.description || agent.agentId }}</p><div class="agent-card-meta"><span>{{ agent.currentVersion || '未发布' }}</span></div><el-button type="primary" plain @click="editAgent(agent.agentId)">编辑配置</el-button></article><button class="agent-card agent-card-add" @click="createAgent"><span>＋</span><strong>新增 Agent</strong><small>创建独立运行配置</small></button><div v-if="!agents.length" class="resource-empty">还没有 Agent</div></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="RAG 绑定" name="rag">
          <RagSettingsPanel :context="props.context" />
        </el-tab-pane>
      </el-tabs>
    </div>
  </el-dialog>

  <ModelSettingsEditors :context="props.context" />
  <McpSettingsEditors :context="props.context" />
  <SkillSettingsEditors :context="props.context" />
  <RagSettingsEditors :context="props.context" />

  <el-dialog v-model="showAgentEditor" class="editor-dialog agent-editor-dialog" modal-class="editor-overlay" width="min(920px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <template #header><div class="editor-dialog-header"><div><span class="eyebrow">AGENT RUNTIME</span><h3>{{ isNewAgent ? '创建 Agent' : (config?.name || 'Agent 配置') }}</h3></div><el-tag effect="plain" round>{{ config?.agentId }}</el-tag></div></template>
    <div v-if="config" class="agent-editor">
      <section class="agent-editor-section">
        <div class="agent-editor-section-heading"><div><span class="eyebrow">PROFILE</span><h4>基础信息</h4><p>先给 Agent 一个清晰的身份，再设置它的运行边界。</p></div><span class="editor-section-index">01</span></div>
        <el-form label-position="top" class="agent-form-grid">
          <el-form-item label="Agent ID"><el-input v-model="config.agentId" :disabled="!isNewAgent" placeholder="例如 customer-support" /><small class="form-help">只能使用字母、数字、点、下划线或短横线。</small></el-form-item>
          <el-form-item label="显示名称"><el-input v-model="config.name" placeholder="例如 客服助手" /></el-form-item>
          <el-form-item label="能力描述" class="span-two"><el-input v-model="config.description" type="textarea" :rows="2" placeholder="说明这个 Agent 擅长处理的请求，供意图识别 Agent 选择。" /></el-form-item>
          <el-form-item label="系统指令" class="span-two"><el-input v-model="config.config.instructions" type="textarea" :rows="4" placeholder="定义 Agent 的角色、边界和输出要求。意图识别 Agent 应要求只返回结构化选择结果。" /></el-form-item>
          <el-form-item label="最大连续轮次"><el-input-number v-model="config.config.maxTurns" :min="1" :max="1000" controls-position="right" /><small class="form-help">限制一次任务中的最大推理轮次。</small></el-form-item>
          <el-form-item label="代码执行"><el-switch :model-value="config.config.codeExecution?.enabled ?? false" @update:model-value="config.config.codeExecution = { enabled: Boolean($event) }" /><small class="form-help">允许分析文件并生成 PPT、Excel 等文件。需要管理员启用隔离执行服务。</small></el-form-item>
          <el-form-item label="发布状态"><div class="agent-readonly-value"><el-tag round effect="plain">{{ config.status === 2 ? 'Published' : config.status === 1 ? 'Pending review' : 'Draft' }}</el-tag><span>版本 {{ config.currentVersion || '尚未发布' }}</span></div></el-form-item>
        </el-form>
      </section>

      <section class="agent-editor-section">
        <div class="agent-editor-section-heading"><div><span class="eyebrow">CAPABILITY BINDINGS</span><h4>能力绑定</h4><p>当前 Agent 的 MCP、Skill、RAG 以卡片展示；勾选即可启用或停用 Skill 与 RAG。</p></div><span class="editor-section-index">02</span></div>
        <div class="binding-groups">
          <article class="binding-group"><div class="binding-group-heading"><div><strong>MCP</strong><small>通过选择窗口绑定 MCP，当前仅显示已绑定项</small></div><el-button link type="primary" :loading="loadingMcpBindingOptions" @click="openMcpBindingPicker">选择 MCP</el-button></div><div v-if="boundMcpServers.length" class="binding-list"><div v-for="server in boundMcpServers" :key="server.name" class="binding-item"><span class="binding-icon mcp-avatar">M</span><div><strong>{{ server.name }}</strong><small>{{ server.type }} · {{ server.url || '配置不存在' }}</small></div><el-button link type="danger" @click="removeMcpBinding(server.name)">移除</el-button></div></div><div v-else class="binding-empty">尚未绑定 MCP，请点击“选择 MCP”。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>Skill</strong><small>通过选择窗口绑定 Skill，当前仅显示已绑定项</small></div><el-button link type="primary" :loading="loadingSkillBindingOptions" @click="openSkillBindingPicker">选择 Skill</el-button></div><div v-if="boundSkills.length" class="binding-list"><div v-for="skill in boundSkills" :key="skill.skillId" class="binding-item"><span class="binding-icon skill-avatar">S</span><div><strong>{{ skill.name || '未命名 Skill' }}</strong><small>{{ skill.skillId }}</small></div><el-button link type="danger" @click="removeSkillBinding(skill.skillId)">移除</el-button></div></div><div v-else class="binding-empty">尚未绑定 Skill，请点击“选择 Skill”。</div></article>
          <article class="binding-group"><div class="binding-group-heading"><div><strong>RAG</strong><small>知识检索数据源</small></div><el-button link type="primary" @click="showAgentEditor = false; openSettings('rag')">管理 RAG</el-button></div><div v-if="ragInstances.length" class="binding-list"><label v-for="rag in ragInstances" :key="rag.id" class="binding-item binding-check-item"><span class="binding-icon rag-avatar">R</span><div><strong>{{ rag.name || rag.id }}</strong><small>{{ rag.type }} · {{ rag.collectionName || '默认数据集' }}</small></div><el-checkbox :model-value="isRagEnabled(rag.id)" @change="toggleRagBinding(rag, Boolean($event))" /></label></div><div v-else class="binding-empty">还没有 RAG，去 RAG 表格中新增。</div></article>
        </div>
      </section>
    </div>
    <template #footer><el-button @click="showAgentEditor = false">取消</el-button><el-button type="primary" :loading="savingConfig" @click="saveConfig">保存 Agent 配置</el-button></template>
  </el-dialog>

</template>
