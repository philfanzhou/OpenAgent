export type ConversationStatus = 'Running' | 'Completed' | 'Failed' | 'Cancelled' | number

export interface AgentSummary {
  agentId: string
  name: string
  status: number
  currentVersion: string
  apiFormat: string
}

export interface CurrentUserContext {
  userId: string
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
  attachments?: MessageAttachment[]
}

export interface MessageAttachment {
  fileName: string
  mediaType: string
  length: number
  previewUrl?: string
}

export interface PendingAttachment {
  id: string
  file: File
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
}

export interface McpServerConfig {
  name: string
  url: string
  type: 'Http' | 'SSE' | 'Stdio'
  command?: string
  arguments?: string[]
  workingDirectory?: string
  environmentVariables?: Record<string, string>
}

export interface SkillInstanceConfig {
  skillId: string
  name: string
  enabled: boolean
  description?: string
  parametersJsonSchema?: string
  type?: string | null
  endpointUrl?: string | null
  version?: string | null
  source?: string
  sourceId?: string | null
  allowedUserIds?: string[]
  allowedGroups?: string[]
  allowedTenantIds?: string[]
  allowedRoles?: string[]
}

export interface SkillsConfig {
  enabledSkills: string[]
  instances: SkillInstanceConfig[]
}

export interface LlmConfig {
  provider: string
  format: 'OpenAIChatCompletions' | 'OpenAIResponses' | 'AnthropicMessages' | string
  modelId: string
  apiKey: string
  endpoint: string
  temperature: number
}

export interface LlmProviderProfile {
  id: string
  name: string
  format: 'OpenAIChatCompletions' | 'OpenAIResponses' | 'AnthropicMessages' | string
  modelId: string
  endpoint: string
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
  mode: 'Basic' | string
  password: { enabled: boolean; endpoint: string }
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
  status: number
  currentVersion: string
  config: {
    llm: LlmConfig
    mcp: { servers: McpServerConfig[] }
    rag: RagConfig
    skills: SkillsConfig
    maxTurns: number
  }
}

export interface StreamEvent {
  type: string
  content?: string
  status?: string
  traceId?: string
  toolName?: string
  toolCallId?: string
  error?: { title?: string; detail?: string; traceId?: string }
  usage?: Record<string, unknown>
}

export interface McpTestResult {
  success: boolean
  connected: boolean
  authorized: boolean
  transport: string
  latencyMs: number
  toolCount: number
  deniedTools: string[]
  error?: string | null
  traceId?: string
}
