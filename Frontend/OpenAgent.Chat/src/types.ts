export type ConversationStatus = 'Running' | 'Completed' | 'Failed' | 'Cancelled' | number
export type ConnectionMode = 'router' | 'engine'
export const AUTO_AGENT_ID = '__auto__'

export interface AgentSummary {
  tenantId: string
  agentId: string
  name: string
  description: string
  status: number
  currentVersion: string
}

export interface CurrentUserContext {
  userId: string
  username?: string
  email?: string
  tenantId?: string
  roles: string[]
  groups: string[]
  audience: string[]
  isAuthenticated: boolean
}

/** 会话消息元数据（后端 ConversationMessageMetadata 的 wire 形态，camelCase）。 */
export interface ConversationMessageMetadata {
  files?: MessageFileMetadata[] | null
  reasoning?: string | null
  /** 中止/失败状态：ConversationStatus 的字符串名（'Cancelled' / 'Failed'）。 */
  executionStatus?: string | null
  /** 持久化的执行失败原因（刷新重载后错误卡片的数据来源）。 */
  error?: MessageErrorMetadata | null
  /** 工具调用参数的原始 JSON 字符串，展示时按需解析。 */
  toolArguments?: string | null
  /** 未知键逃生舱（含载荷解析失败时保留的原始键值）。 */
  extensions?: Record<string, string> | null
}

/** 后端 MessageErrorMetadata：失败标题 + 可执行建议 + 排查用 TraceId。 */
export interface MessageErrorMetadata {
  title?: string | null
  detail?: string | null
  traceId?: string | null
}

/** 消息附件的持久化描述（后端 MessageFileMetadata）。 */
export interface MessageFileMetadata {
  fileId: string
  fileName: string
  mediaType: string
  length: number
  objectKey?: string | null
}

export interface PlanStep {
  step: string
  status: 'pending' | 'in_progress' | 'completed'
}

/** update_plan 工具结果 / plan_updated 事件的计划快照（模型每次全量重发）。 */
export interface PlanSnapshot {
  plan: PlanStep[]
  completed: number
  total: number
}

export interface ConversationMessage {
  messageId: string
  sequence: number
  role: string
  content: string
  /** UI-only 任务计划快照：从 update_plan 工具结果 / plan_updated 事件投影。 */
  plan?: PlanSnapshot
  toolCallId?: string
  toolName?: string
  idempotencyKey?: string
  timestamp: string
  /** 产生该消息的轮次追溯键（X-Trace-Id），历史消息可能缺失。 */
  traceId?: string
  metadata?: ConversationMessageMetadata
  reasoning?: string
  toolActivities?: ToolActivity[]
  /** UI-only ordered execution trace assembled from reasoning and tool messages. */
  processActivities?: ProcessActivity[]
  files?: MessageFile[]
  fileIds?: string[]
  tokenUsage?: TokenUsage
  modelId?: string
  /** 执行失败的独立展示，不写入会话历史。 */
  error?: { title?: string; detail?: string; traceId?: string }
}

export interface ToolActivity {
  name: string
  callId?: string
  result?: string
  /** 工具调用参数（流式下发或从历史 metadata.toolArguments 解析）。 */
  arguments?: unknown
}

export type ProcessActivity =
  | { kind: 'reasoning'; content: string }
  | { kind: 'tool'; tool: ToolActivity }

/** 附件的 UI 投影：wire 形态为 MessageFileMetadata，预览字段由前端运行时补充。 */
export interface MessageFile {
  fileId?: string
  fileName: string
  mediaType: string
  length: number
  /** 对象存储键，用于 markdown 预览时相对解析同批 S3 图片。 */
  objectKey?: string
  previewUrl?: string
  previewText?: string
}

export interface FileAsset {
  fileId: string
  tenantId: string
  ownerUserId: string
  fileName: string
  mediaType: string
  length: number
  sha256: string
  objectKey: string
  source: 'UserUpload' | 'Agent' | 'Skill' | number
  state: 'Pending' | 'Ready' | 'Failed' | number
  createdAt: string
}

export interface PendingFile {
  id: string
  file: File
  state: 'uploading' | 'ready' | 'failed'
  asset?: FileAsset
  error?: string
}

export interface ConversationRecord {
  conversationId: string
  tenantId: string
  userId: string
  agentId?: string
  /** ConversationType 数值形态（0=User），始终序列化。 */
  type: number
  status: ConversationStatus
  /** 乐观并发版本，始终序列化。 */
  version: number
  isDeletedByUser: boolean
  deletedAt?: string | null
  traceId?: string | null
  createdAt: string
  updatedAt: string
  lastMessageAt: string
  messageCount: number
  title?: string
  messages?: ConversationMessage[]
  contextSummaries?: ContextSummary[]
}

export interface ContextSummary {
  compressionId: string
  strategy: string
  trigger: 'Automatic' | 'Manual' | string
  status: 'Succeeded' | 'Skipped' | 'Failed' | string
  summary?: string | null
  result?: string | null
  error?: string | null
  lastCompressedAt: string
  compressedMessageCount: number
  originalStartSequence: number
  originalEndSequence: number
  originalTokenCount: number
  tokenCount: number
  originalHistoryRestored: boolean
  sourceEndSequence: number
  compactedMessages?: ConversationMessage[]
}

export interface TokenUsage {
  promptTokens: number
  completionTokens: number
  totalTokens: number
  cachedInputTokens?: number | null
  reasoningTokens?: number | null
}

/** 一次大模型交互的完整脱敏日志（后端 llm_interaction_logs 记录）。 */
export interface LlmInteractionRecord {
  interactionId: string
  tenantId: string
  userId: string
  conversationId?: string | null
  traceId: string
  agentId?: string | null
  /** 0=AgentTurn（对话轮次），1=Compaction（压缩摘要）。 */
  source: number
  provider?: string | null
  apiFormat?: string | null
  modelId: string
  streamed: boolean
  callIndex: number
  /** 脱敏后的请求载荷 JSON 字符串（messages + options）。 */
  requestJson?: string | null
  /** 脱敏后的响应载荷 JSON 字符串（contents + usage）；失败时可能为空。 */
  responseJson?: string | null
  tokenUsage?: TokenUsage | null
  /** 0=Succeeded，1=Failed，2=Cancelled。 */
  status: number
  errorMessage?: string | null
  startedAt: string
  durationMs: number
}

export interface McpServerConfig {
  name: string
  url: string
  type: 'Http' | 'SSE'
  protocolVersion?: string | null
}

export interface McpConfig {
  enabledServerIds: string[]
  servers: McpServerConfig[]
}

export interface SkillInstanceConfig {
  skillId: string
  name: string
  enabled: boolean
  description?: string
  source?: string
  sourceId?: string | null
  packageFileName?: string | null
  packageFormat?: string | null
  objectKey?: string | null
  sha256?: string | null
  resourceCount?: number
  scriptExecutionEnabled?: boolean
  scriptNames?: string[]
  scriptCount?: number
  allowedUserIds?: string[]
  allowedGroups?: string[]
  allowedTenantIds?: string[]
  allowedRoles?: string[]
}

export type SkillCatalogItem = SkillInstanceConfig

export interface SkillsConfig {
  enabledSkills: string[]
  instances: SkillInstanceConfig[]
}

export interface LlmProviderProfile {
  id: string
  name: string
  format: 'OpenAIChatCompletions' | 'OpenAIResponses' | 'AnthropicMessages' | string
  modelId: string
  contextTokens: number
  endpoint: string
  apiKey: string
  temperature: number
  modality: 'Text' | 'Multimodal' | string
}

export interface LlmTestResult {
  success: boolean
  connected: boolean
  statusCode?: number | null
  latencyMs: number
  modelId?: string | null
  error?: string | null
  traceId?: string | null
}

export interface RagInstanceConfig {
  id: string
  name: string
  enabled: boolean
  type: string
  collectionName: string
  apiEndpoint: string
  apiKeySecretRef?: string
  apiKey?: string
  adapterConfig?: Record<string, string> | null
  allowedUserIds?: string[]
  allowedGroups?: string[]
  allowedTenantIds?: string[]
  allowedRoles?: string[]
}

export interface RagConfig {
  enabled: boolean
  enabledRagInstanceIds: string[]
  instances: RagInstanceConfig[]
}

export interface RagTestResult {
  success: boolean
  connected: boolean
  statusCode?: number | null
  latencyMs: number
  error?: string | null
  traceId?: string | null
}

export interface AuthConfig {
  mode: 'Basic' | 'JwtBearer' | string
  development: boolean
  keycloak?: { enabled: boolean }
  password: { enabled: boolean; endpoint: string }
  anonymous: { enabled: boolean }
  oidc?: {
    authority: string
    clientId: string
    audience: string
    scopes: string[]
  } | null
}

export interface AuthTokenResponse {
  access_token: string
  token_type?: string
  expires_in?: number
  refresh_token?: string
}

export interface AgentConfigEntity {
  agentId: string
  name: string
  description: string
  status: number
  currentVersion: string
  config: {
    instructions: string
    mcp: { enabledServerIds?: string[]; servers: McpServerConfig[] }
    rag: RagConfig
    skills: SkillsConfig
    codeExecution?: { enabled: boolean }
    maxTurns: number
  }
}

export interface StreamEvent {
  type: string
  content?: string
  agentId?: string
  status?: string
  traceId?: string
  toolName?: string
  toolCallId?: string
  toolArguments?: unknown
  conversationId?: string
  error?: { title?: string; detail?: string; traceId?: string }
  usage?: TokenUsage | null
  modelId?: string | null
}

export interface McpTestResult {
  success: boolean
  connected: boolean
  authorized: boolean
  transport: string
  requestedProtocolVersion?: string | null
  negotiatedProtocolVersion?: string | null
  latencyMs: number
  toolCount: number
  deniedTools: string[]
  error?: string | null
  traceId?: string
}

export interface SkillPackageInstallResponse {
  skill: SkillInstanceConfig
  currentVersion: string
  storage: string
}

export interface SkillTestResult {
  success: boolean
  enabledCount: number
  instanceCount: number
  objectStorageVerifiedSkills: string[]
  invalidSkills: string[]
}

export interface HealthReportItem {
  key: string
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  detail?: string
  latencyMs?: number
  data?: Record<string, unknown>
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  service?: string
  totalDurationMs?: number
  items: HealthReportItem[]
}

export interface HealthEntry {
  status: string
  description?: string
  duration?: string
  data?: Record<string, unknown>
}

export interface NativeHealthReport {
  status: string
  entries: Record<string, HealthEntry>
  totalDuration?: string
}
