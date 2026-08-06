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

export interface AgentConfigEntity {
  agentId: string
  name: string
  status: number
  currentVersion: string
  config: {
    llm: Record<string, unknown>
    mcp: { servers: McpServerConfig[] }
    rag: Record<string, unknown>
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
