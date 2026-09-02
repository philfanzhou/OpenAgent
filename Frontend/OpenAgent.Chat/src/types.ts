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
  apiFormat: string
  llmProvider?: string
  llmModel?: string
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

export interface ConversationMessage {
  messageId: string
  sequence: number
  role: string
  content: string
  toolCallId?: string
  toolName?: string
  timestamp: string
  metadata?: Record<string, string>
  reasoning?: string
  toolActivities?: ToolActivity[]
  /** UI-only ordered execution trace assembled from reasoning and tool messages. */
  processActivities?: ProcessActivity[]
  files?: MessageFile[]
  tokenUsage?: TokenUsage
  modelId?: string
  /** 执行失败的独立展示，不写入会话历史。 */
  error?: { title?: string; detail?: string; traceId?: string }
}

export interface ToolActivity {
  name: string
  callId?: string
  result?: string
  /** 工具调用参数（流式下发或从历史 metadata.ToolArguments 解析）。 */
  arguments?: unknown
}

export type ProcessActivity =
  | { kind: 'reasoning'; content: string }
  | { kind: 'tool'; tool: ToolActivity }

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
  objectKey?: string
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
  status: ConversationStatus
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

export interface LlmConfig {
  provider: string
  format: 'OpenAIChatCompletions' | 'OpenAIResponses' | 'AnthropicMessages' | string
  modelId: string
  apiKeySecretRef: string
  apiKey: string
  endpoint: string
  temperature: number
}

export interface LlmProviderProfile {
  id: string
  name: string
  format: 'OpenAIChatCompletions' | 'OpenAIResponses' | 'AnthropicMessages' | string
  modelId?: string | null
  endpoint: string
  apiKeySecretRef: string
  apiKey: string
  temperature: number
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
    llm: LlmConfig
    mcp: { enabledServerIds?: string[]; servers: McpServerConfig[] }
    rag: RagConfig
    skills: SkillsConfig
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
