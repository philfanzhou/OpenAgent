<script setup lang="ts">
import type { Ref } from 'vue'
import HealthCheckPanel from './HealthCheckPanel.vue'
import type { useSettings } from '../composables/useSettings'
import type { AgentSummary, AuthConfig, ConnectionMode, CurrentUserContext } from '../types'

type SettingsDialogContext = ReturnType<typeof useSettings> & {
  connectionMode: Ref<ConnectionMode>
  routerUrl: Ref<string>
  engineUrl: Ref<string>
  tenantId: Ref<string>
  statusText: Ref<string>
  authConfig: Ref<AuthConfig | null>
  currentUser: Ref<CurrentUserContext | null>
  agents: Ref<AgentSummary[]>
  selectedAgentId: Ref<string>
  activeEndpointLabel: Ref<string>
  activeEndpointHost: Ref<string>
  connect: () => Promise<void>
  logout: () => Promise<void>
  testHealth: (path: '/health' | '/ready') => Promise<void>
}

const props = defineProps<{ context: SettingsDialogContext }>()
const {
  connectionMode,
  routerUrl,
  engineUrl,
  tenantId,
  statusText,
  authConfig,
  currentUser,
  agents,
  selectedAgentId,
  activeEndpointLabel,
  activeEndpointHost,
  connect,
  logout,
  testHealth,
  showSettings,
  activeSettings,
  savingConfig,
  refreshingAgents,
  testingMcp,
  uploadingSkill,
  testingRag,
  config,
  showAgentEditor,
  isNewAgent,
  showMcpEditor,
  showRagEditor,
  mcpDraft,
  mcpServers,
  agentMcpIds,
  showMcpBindingPicker,
  mcpBindingOptions,
  loadingMcpBindingOptions,
  selectedMcpIndex,
  mcpResult,
  skillPackageInput,
  showSkillTextEditor,
  skillMarkdownDraft,
  skillEditorMode,
  skillEditorName,
  skillEditorDescription,
  skillEditorInstructions,
  editingSkillId,
  showSkillBindingPicker,
  skillBindingOptions,
  loadingSkillBindingOptions,
  skillCatalog,
  skillDraft,
  ragDraft,
  ragInstances,
  selectedRagIndex,
  ragResult,
  llmProfiles,
  llmDraft,
  selectedLlmIndex,
  llmResult,
  testingLlm,
  savingLlm,
  showLlmEditor,
  isNewLlm,
  boundMcpServers,
  boundSkills,
  ragEnabledText,
  isSkillEnabled,
  toggleSkillBinding,
  toggleMcpBinding,
  openMcpBindingPicker,
  openSkillBindingPicker,
  removeMcpBinding,
  removeSkillBinding,
  isRagEnabled,
  toggleRagBinding,
  loadLlmProfiles,
  loadMcpProfiles,
  loadSkillCatalog,
  selectLlm,
  newLlm,
  editLlm,
  deleteLlm,
  saveLlm,
  testLlm,
  refreshAgents,
  loadConfig,
  selectMcp,
  newMcp,
  removeMcp,
  editAgent,
  chooseSkillPackage,
  openSkillTextEditor,
  switchSkillEditorMode,
  editSkill,
  uploadSkillPackage,
  deleteSkillCatalog,
  saveTextSkill,
  selectRag,
  newRag,
  editRag,
  saveRag,
  deleteRag,
  testRag,
  testRagRow,
  createAgent,
  saveConfig,
  saveMcp,
  testMcp,
  handleAgentChange,
  selectAgent,
  openSettings,
  handleSettingsTabChange,
  resetSettings,
} = props.context
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
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MODEL PROFILES</span><h3>大模型配置</h3><p>按租户维护模型、上下文大小、协议、Endpoint 和密钥；执行时独立选择，不与 Agent 强制绑定。</p></div><div class="section-actions"><el-button @click="loadLlmProfiles">刷新</el-button><el-button type="primary" plain @click="newLlm">新增配置</el-button></div></div>
            <el-table :data="llmProfiles" class="capability-table" empty-text="还没有大模型配置"><el-table-column label="名称" min-width="140"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="模型" min-width="150"><template #default="scope">{{ scope.row.modelId }}</template></el-table-column><el-table-column label="能力" width="110"><template #default="scope"><el-tag size="small" round>{{ scope.row.modality === 'Multimodal' ? '多模态' : '文本' }}</el-tag></template></el-table-column><el-table-column label="上下文" width="120"><template #default="scope">{{ scope.row.contextWindowTokens.toLocaleString() }}</template></el-table-column><el-table-column label="协议" width="160"><template #default="scope"><el-tag size="small" round>{{ scope.row.format }}</el-tag></template></el-table-column><el-table-column label="Endpoint" min-width="200" show-overflow-tooltip><template #default="scope">{{ scope.row.endpoint }}</template></el-table-column><el-table-column label="密钥" width="100"><template #default>已保护</template></el-table-column><el-table-column label="操作" width="160" fixed="right"><template #default="scope"><el-button link type="primary" @click="editLlm(scope.$index)">编辑</el-button><el-button link @click="selectLlm(scope.$index); testLlm(); showLlmEditor = true">测试</el-button><el-button link type="danger" @click="selectLlm(scope.$index); deleteLlm()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="MCP 配置" name="mcp">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">MCP CATALOG</span><h3>MCP 配置</h3><p>独立维护远程 MCP Server；Agent 页面只选择已注册的 Server，不在 Agent 中复制 Endpoint。</p></div><div class="section-actions"><el-button @click="loadMcpProfiles">刷新</el-button><el-button type="primary" plain @click="newMcp">新增 MCP</el-button></div></div>
            <el-table :data="mcpServers" class="capability-table" empty-text="还没有 MCP 配置"><el-table-column label="名称" min-width="160"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.url || '未配置' }}</template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="selectMcp(scope.$index); showMcpEditor = true">编辑</el-button><el-button link @click="selectMcp(scope.$index); testMcp(); showMcpEditor = true">测试</el-button><el-button link type="danger" @click="removeMcp(scope.$index)">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Skill 配置" name="skill">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">SKILL CATALOG</span><h3>Skill 配置</h3><p>独立维护官方 Skill 目录；Agent 页面只选择 Skill ID，绑定关系保存到 Agent 配置。</p></div><div class="section-actions"><input ref="skillPackageInput" type="file" hidden accept=".zip,.md" @change="uploadSkillPackage" /><el-button @click="loadSkillCatalog">刷新</el-button><el-button :loading="uploadingSkill" type="primary" plain @click="chooseSkillPackage">上传 ZIP / MD</el-button><el-button type="primary" plain @click="openSkillTextEditor">手动填写</el-button></div></div>
            <el-table :data="skillCatalog" class="capability-table" empty-text="还没有 Skill"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name }}</strong><small class="table-subtext">{{ scope.row.skillId }}</small></template></el-table-column><el-table-column label="说明" min-width="240" show-overflow-tooltip><template #default="scope">{{ scope.row.description }}</template></el-table-column><el-table-column label="内容" width="120"><template #default="scope">{{ scope.row.resourceCount || 0 }} 资源</template></el-table-column><el-table-column label="来源" width="150"><template #default="scope">{{ scope.row.packageFileName }}</template></el-table-column><el-table-column label="操作" width="170" fixed="right"><template #default="scope"><el-button link type="primary" @click="editSkill(scope.row)">编辑</el-button><el-button link type="danger" @click="deleteSkillCatalog(scope.row)">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
        <el-tab-pane label="Agent 配置" name="agent">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">AGENT RUNTIME</span><h3>Agent 配置</h3><p>Agent 只维护指令、运行边界和能力绑定；模型在每次执行时独立选择。</p></div><div class="section-actions"><el-button @click="refreshAgents" :loading="refreshingAgents">刷新 Agent</el-button><el-button type="primary" plain @click="createAgent">新增 Agent</el-button></div></div>
            <div class="agent-card-grid"><article v-for="agent in agents" :key="agent.agentId" class="agent-card"><h4>{{ agent.name || agent.agentId }}</h4><p>{{ agent.description || agent.agentId }}</p><div class="agent-card-meta"><span>{{ agent.currentVersion || '未发布' }}</span></div><el-button type="primary" plain @click="editAgent(agent.agentId)">编辑配置</el-button></article><button class="agent-card agent-card-add" @click="createAgent"><span>＋</span><strong>新增 Agent</strong><small>创建独立运行配置</small></button><div v-if="!agents.length" class="resource-empty">还没有 Agent</div></div>
          </section>
        </el-tab-pane>
        <el-tab-pane label="RAG 绑定" name="rag">
          <section class="settings-section"><div class="section-heading"><div><span class="eyebrow">CAPABILITIES</span><h3>RAG 绑定</h3><p>按表格维护检索实例，并可逐条测试 RAG 服务地址。</p></div><div class="section-actions"><el-button type="primary" plain @click="newRag">新增 RAG</el-button></div></div>
            <div class="capability-summary"><strong>{{ ragEnabledText }}</strong></div><el-table :data="ragInstances" class="capability-table" empty-text="还没有绑定 RAG"><el-table-column label="名称" min-width="180"><template #default="scope"><strong>{{ scope.row.name || scope.row.id }}</strong><small class="table-subtext">{{ scope.row.id }}</small></template></el-table-column><el-table-column label="类型" width="130"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="Endpoint" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.apiEndpoint || '未配置' }}</template></el-table-column><el-table-column label="状态" width="110"><template #default="scope"><span class="table-status" :class="{ muted: !isRagEnabled(scope.row.id) }"><i />{{ isRagEnabled(scope.row.id) ? '已绑定' : '未绑定' }}</span></template></el-table-column><el-table-column label="操作" width="190" fixed="right"><template #default="scope"><el-button link type="primary" @click="editRag(scope.$index)">编辑</el-button><el-button link @click="testRagRow(scope.$index)">测试</el-button><el-button link type="danger" @click="selectRag(scope.$index); deleteRag()">删除</el-button></template></el-table-column></el-table>
          </section>
        </el-tab-pane>
      </el-tabs>
    </div>
  </el-dialog>

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

  <el-dialog v-model="showMcpBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 MCP" width="min(760px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="mcpBindingOptions" max-height="440" empty-text="还没有可绑定的 MCP"><el-table-column label="名称" min-width="170"><template #default="scope"><strong>{{ scope.row.name }}</strong></template></el-table-column><el-table-column label="类型" width="120"><template #default="scope"><el-tag size="small" round>{{ scope.row.type }}</el-tag></template></el-table-column><el-table-column label="地址" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.url }}</template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="agentMcpIds.some(id => id.toLowerCase() === scope.row.name.toLowerCase())" @change="toggleMcpBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showMcpBindingPicker = false">完成</el-button></template>
  </el-dialog>

  <el-dialog v-model="showSkillBindingPicker" class="editor-dialog" modal-class="editor-overlay" title="选择 Skill" width="min(820px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-table :data="skillBindingOptions" max-height="440" empty-text="还没有可绑定的 Skill"><el-table-column label="名称" min-width="190"><template #default="scope"><strong>{{ scope.row.name || '未命名 Skill' }}</strong><small class="table-subtext">{{ scope.row.skillId }}</small></template></el-table-column><el-table-column label="说明" min-width="260" show-overflow-tooltip><template #default="scope">{{ scope.row.description }}</template></el-table-column><el-table-column label="资源" width="100"><template #default="scope">{{ scope.row.resourceCount || 0 }}</template></el-table-column><el-table-column label="绑定" width="90"><template #default="scope"><el-checkbox :model-value="isSkillEnabled(scope.row.skillId)" @change="toggleSkillBinding(scope.row, Boolean($event))" /></template></el-table-column></el-table>
    <template #footer><el-button @click="showSkillBindingPicker = false">完成</el-button></template>
  </el-dialog>

  <el-dialog v-model="showLlmEditor" class="editor-dialog" modal-class="editor-overlay" :title="isNewLlm ? '新增大模型配置' : '编辑大模型配置'" width="min(720px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top" class="agent-form-grid"><el-form-item label="配置 ID"><el-input v-model="llmDraft.id" :disabled="!isNewLlm" placeholder="例如 openai-gpt4o" /><small class="form-help">执行请求通过这个 ID 选择模型配置。</small></el-form-item><el-form-item label="显示名称"><el-input v-model="llmDraft.name" placeholder="例如 OpenAI GPT-4o" /></el-form-item><el-form-item label="模型 ID"><el-input v-model="llmDraft.modelId" placeholder="例如 gpt-4o" /></el-form-item><el-form-item label="模型能力"><el-select v-model="llmDraft.modality" class="full-width"><el-option label="文本" value="Text" /><el-option label="多模态（当前仅图片）" value="Multimodal" /></el-select><small class="form-help">多模态模型会将受限大小的图片直接发送给模型；音频、视频等输入暂不开放。</small></el-form-item><el-form-item label="上下文大小"><el-input-number v-model="llmDraft.contextWindowTokens" :min="1" :step="1024" controls-position="right" /><small class="form-help">模型可接受的最大上下文 token 数。</small></el-form-item><el-form-item label="API 格式"><el-select v-model="llmDraft.format" class="full-width"><el-option label="OpenAI Chat Completions" value="OpenAIChatCompletions" /><el-option label="OpenAI Responses" value="OpenAIResponses" /><el-option label="Anthropic Messages" value="AnthropicMessages" /></el-select></el-form-item><el-form-item label="Temperature"><el-input-number v-model="llmDraft.temperature" :min="0" :max="2" :step="0.1" :precision="1" controls-position="right" /></el-form-item><el-form-item label="Endpoint" class="span-two"><el-input v-model="llmDraft.endpoint" placeholder="https://api.openai.com/v1" /></el-form-item><el-form-item label="API Key" class="span-two"><el-input v-model="llmDraft.apiKey" type="password" show-password autocomplete="new-password" placeholder="编辑时留空表示保留现有密钥" /><small class="form-help">密钥以明文保存在租户隔离的服务端配置中，但查询响应永不返回；测试会使用已保存的真实密钥。</small></el-form-item><el-alert v-if="llmResult" class="span-two" :title="`测试结果：${llmResult.success ? '连接和权限通过' : '连接失败'}${llmResult.statusCode ? ` · HTTP ${llmResult.statusCode}` : ''}`" :description="llmResult.error || `模型 ${llmResult.modelId || llmDraft.modelId} · 延迟 ${llmResult.latencyMs}ms`" :type="llmResult.success ? 'success' : 'warning'" :closable="false" /></el-form>
    <template #footer><el-button @click="showLlmEditor = false">取消</el-button><el-button :loading="testingLlm" @click="testLlm">测试连接与权限</el-button><el-button type="primary" :loading="savingLlm" :disabled="!llmDraft.id" @click="saveLlm">保存大模型配置</el-button></template>
  </el-dialog>

  <el-dialog v-model="showMcpEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 MCP" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top">
      <el-form-item label="名称"><el-input v-model="mcpDraft.name" :disabled="selectedMcpIndex >= 0" placeholder="例如 local-tools" /><small class="form-help">名称同时作为 MCP 配置 ID；编辑时不可修改，避免已有 Agent 绑定失效。</small></el-form-item>
      <el-form-item label="传输类型"><el-select v-model="mcpDraft.type"><el-option label="Streamable HTTP" value="Http" /><el-option label="Legacy SSE" value="SSE" /></el-select></el-form-item>
      <el-form-item label="MCP 协议版本"><el-select v-model="mcpDraft.protocolVersion" clearable filterable allow-create placeholder="自动协商（推荐）"><el-option label="2026-07-28" value="2026-07-28" /><el-option label="2025-11-25" value="2025-11-25" /><el-option label="2025-06-18" value="2025-06-18" /><el-option label="2025-03-26" value="2025-03-26" /><el-option label="2024-11-05" value="2024-11-05" /></el-select><small class="form-help">留空由官方 SDK 自动协商；指定版本作为最低版本，服务器降级到更早版本时连接会失败。</small></el-form-item>
      <el-form-item label="URL"><el-input v-model="mcpDraft.url" placeholder="https://mcp.example.com/mcp" /></el-form-item>
      <el-alert v-if="mcpResult" :title="`测试结果：${mcpResult.success ? '连接成功' : '连接失败'} · 权限${mcpResult.authorized ? '通过' : '拒绝'}`" :description="mcpResult.error || `协商版本 ${mcpResult.negotiatedProtocolVersion || '未知'} · ${mcpResult.latencyMs}ms · ${mcpResult.toolCount} 个工具`" :type="mcpResult.success ? 'success' : 'warning'" :closable="false" />
    </el-form><template #footer><el-button @click="showMcpEditor = false">取消</el-button><el-button :loading="testingMcp" @click="testMcp">测试连接、版本与权限</el-button><el-button type="primary" :disabled="!mcpDraft.name || !mcpDraft.url" @click="saveMcp">保存 MCP 配置</el-button></template>
  </el-dialog>

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

  <el-dialog v-model="showRagEditor" class="editor-dialog" modal-class="editor-overlay" title="编辑 RAG" width="min(650px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <el-form label-position="top"><el-form-item label="RAG ID"><el-input v-model="ragDraft.id" placeholder="例如 knowledge-base" /></el-form-item><el-form-item label="名称"><el-input v-model="ragDraft.name" placeholder="例如 企业知识库" /></el-form-item><el-form-item label="类型"><el-select v-model="ragDraft.type"><el-option label="RAGFlow" value="ragflow" /><el-option label="Qdrant" value="qdrant" /></el-select></el-form-item><el-form-item label="Endpoint"><el-input v-model="ragDraft.apiEndpoint" placeholder="https://rag.example.com/api/search" /></el-form-item><el-form-item label="Collection / Dataset"><el-input v-model="ragDraft.collectionName" /></el-form-item><el-form-item label="API Key Secret 引用"><el-input v-model="ragDraft.apiKeySecretRef" placeholder="例如 rag:knowledge-base" /></el-form-item><el-form-item label="状态"><el-switch v-model="ragDraft.enabled" active-text="启用" inactive-text="停用" /></el-form-item><el-alert v-if="ragResult" :title="`测试结果：${ragResult.success ? '连接成功' : '连接失败'}`" :description="ragResult.error || `HTTP ${ragResult.statusCode || '-'} · 延迟 ${ragResult.latencyMs}ms`" :type="ragResult.success ? 'success' : 'warning'" :closable="false" /></el-form><template #footer><el-button @click="showRagEditor = false">取消</el-button><el-button :loading="testingRag" @click="testRag">测试连接</el-button><el-button type="primary" :disabled="!ragDraft.id" @click="saveRag">保存 RAG 配置</el-button></template>
  </el-dialog>
</template>
