import type {
  AgentConfigEntity,
  AgentSummary,
  AuthConfig,
  AuthTokenResponse,
  ConversationMessage,
  ConversationRecord,
  CurrentUserContext,
  LlmProviderProfile,
  LlmTestResult,
  McpServerConfig,
  McpTestResult,
  RagConfig,
  RagInstanceConfig,
  RagTestResult,
  SkillsConfig,
  StreamEvent,
} from './types'

const engineStorageKey = 'openagent.engine.base-url'
const tokenStorageKey = 'openagent.auth.access-token'
const tokenTypeStorageKey = 'openagent.auth.token-type'
const tenantStorageKey = 'openagent.auth.tenant-id'

export function getEngineBaseUrl(): string {
  return localStorage.getItem(engineStorageKey) || ''
}

export function setEngineBaseUrl(value: string): void {
  const normalized = value.trim().replace(/\/$/, '')
  localStorage.setItem(engineStorageKey, normalized)
}

export function getAccessToken(): string {
  return sessionStorage.getItem(tokenStorageKey) || ''
}

export function setAccessToken(value: string, tokenType = 'Basic'): void {
  if (value.trim()) {
    sessionStorage.setItem(tokenStorageKey, value.trim())
    sessionStorage.setItem(tokenTypeStorageKey, tokenType.trim() || 'Basic')
  } else {
    sessionStorage.removeItem(tokenStorageKey)
    sessionStorage.removeItem(tokenTypeStorageKey)
  }
}

export function getTenantId(): string {
  return localStorage.getItem(tenantStorageKey) || ''
}

export function setTenantId(value: string): void {
  localStorage.setItem(tenantStorageKey, value.trim())
}

function requireBaseUrl(): string {
  const value = getEngineBaseUrl()
  if (!value) throw new Error('请先在设置中输入 Engine 地址')
  return value
}

function headers(extra: HeadersInit = {}): Headers {
  const result = new Headers({
    Accept: 'application/json',
    ...extra,
  })
  const token = getAccessToken()
  const tenantId = getTenantId()
  if (token) result.set('Authorization', `${sessionStorage.getItem(tokenTypeStorageKey) || 'Basic'} ${token}`)
  if (tenantId) result.set('X-Tenant-Id', tenantId)
  result.set('X-Trace-Id', crypto.randomUUID())
  return result
}

async function readError(response: Response): Promise<Error> {
  try {
    const body = await response.json() as { detail?: string; title?: string; traceId?: string }
    return new Error(`${body.detail || body.title || response.statusText}${body.traceId ? ` (TraceId: ${body.traceId})` : ''}`)
  } catch {
    return new Error(`${response.status} ${response.statusText}`)
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${requireBaseUrl()}${path}`, {
    ...init,
    headers: headers(init.headers),
  })
  if (!response.ok) throw await readError(response)
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

function parseSseBlock(block: string): StreamEvent | null {
  let eventType = 'message'
  const data: string[] = []
  for (const line of block.split(/\r?\n/)) {
    if (line.startsWith(':')) continue
    if (line.startsWith('event:')) eventType = line.slice(6).trim()
    else if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
  }
  if (!data.length) return null

  const payload = JSON.parse(data.join('\n')) as Record<string, unknown>
  if (eventType === 'error' && typeof payload.detail === 'string') {
    return { type: 'error', error: payload as StreamEvent['error'] }
  }
  return { ...payload, type: eventType } as StreamEvent
}

export const api = {
  getAuthConfig(): Promise<AuthConfig> {
    return request<AuthConfig>('/api/v1/auth/config')
  },

  passwordLogin(username: string, password: string): Promise<AuthTokenResponse> {
    return request<AuthTokenResponse>('/api/v1/auth/password/token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    })
  },

  async health(path: '/health' | '/ready'): Promise<void> {
    const response = await fetch(`${requireBaseUrl()}${path}`, { headers: headers() })
    if (!response.ok) throw await readError(response)
  },

  listAgents(): Promise<AgentSummary[]> {
    return request<AgentSummary[]>('/api/v1/agent/agents')
  },

  getCurrentUser(): Promise<CurrentUserContext> {
    return request<CurrentUserContext>('/api/v1/agent/me')
  },

  listConversations(): Promise<ConversationRecord[]> {
    return request<ConversationRecord[]>('/api/v1/agent/conversations?skip=0&take=1000')
  },

  getConversation(id: string): Promise<ConversationRecord> {
    return request<ConversationRecord>(`/api/v1/agent/conversations/${encodeURIComponent(id)}`)
  },

  deleteConversation(id: string): Promise<void> {
    return request<void>(`/api/v1/agent/conversations/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },

  getAgentConfig(id: string): Promise<AgentConfigEntity> {
    return request<AgentConfigEntity>(`/api/v1/admin/agents/${encodeURIComponent(id)}`)
  },

  saveAgentConfig(id: string, config: AgentConfigEntity): Promise<AgentConfigEntity> {
    return request<AgentConfigEntity>(`/api/v1/admin/agents/${encodeURIComponent(id)}/config`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(config),
    })
  },

  listLlmProfiles(): Promise<LlmProviderProfile[]> {
    return request<LlmProviderProfile[]>('/api/v1/admin/llm')
  },

  saveLlmProfile(id: string, profile: LlmProviderProfile): Promise<LlmProviderProfile> {
    return request<LlmProviderProfile>(`/api/v1/admin/llm/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(profile),
    })
  },

  deleteLlmProfile(id: string): Promise<void> {
    return request<void>(`/api/v1/admin/llm/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },

  testLlmProfile(profile: LlmProviderProfile): Promise<LlmTestResult> {
    return request<LlmTestResult>('/api/v1/admin/llm/test-connection', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ profile }),
    })
  },

  saveMcp(id: string, agentId: string, server: McpServerConfig): Promise<McpServerConfig> {
    return request<McpServerConfig>(`/api/v1/admin/mcp/${encodeURIComponent(id)}?agentId=${encodeURIComponent(agentId)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(server),
    })
  },

  getMcpConfig(agentId: string): Promise<{ servers: McpServerConfig[] }> {
    return request<{ servers: McpServerConfig[] }>(`/api/v1/admin/mcp?agentId=${encodeURIComponent(agentId)}`)
  },

  deleteMcp(id: string, agentId: string): Promise<void> {
    return request<void>(`/api/v1/admin/mcp/${encodeURIComponent(id)}?agentId=${encodeURIComponent(agentId)}`, { method: 'DELETE' })
  },

  saveSkills(agentId: string, skills: AgentConfigEntity['config']['skills']): Promise<AgentConfigEntity['config']['skills']> {
    return request<AgentConfigEntity['config']['skills']>(`/api/v1/admin/skills/${encodeURIComponent(agentId)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(skills),
    })
  },

  getSkillsConfig(agentId: string): Promise<SkillsConfig> {
    return request<SkillsConfig>(`/api/v1/admin/skills?agentId=${encodeURIComponent(agentId)}`)
  },

  testSkills(skills: AgentConfigEntity['config']['skills']): Promise<Record<string, unknown>> {
    return request<Record<string, unknown>>('/api/v1/admin/skills/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(skills),
    })
  },

  testMcp(server: McpServerConfig, agentId?: string): Promise<McpTestResult> {
    return request<McpTestResult>('/api/v1/admin/mcp/test-connection', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ agentId, server, action: 'discover' }),
    })
  },

  getRagConfig(agentId: string): Promise<RagConfig> {
    return request<RagConfig>(`/api/v1/admin/rag?agentId=${encodeURIComponent(agentId)}`)
  },

  saveRag(id: string, agentId: string, instance: RagInstanceConfig): Promise<RagInstanceConfig> {
    return request<RagInstanceConfig>(`/api/v1/admin/rag/${encodeURIComponent(id)}?agentId=${encodeURIComponent(agentId)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(instance),
    })
  },

  deleteRag(id: string, agentId: string): Promise<void> {
    return request<void>(`/api/v1/admin/rag/${encodeURIComponent(id)}?agentId=${encodeURIComponent(agentId)}`, { method: 'DELETE' })
  },

  testRag(instance: RagInstanceConfig): Promise<RagTestResult> {
    return request<RagTestResult>('/api/v1/admin/rag/test-connection', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ instance }),
    })
  },

  async *streamChat(message: string, agentId?: string, conversationId?: string, attachments: File[] = []): AsyncGenerator<StreamEvent> {
    const hasAttachments = attachments.length > 0
    const form = new FormData()
    form.set('message', message)
    if (agentId) form.set('agentId', agentId)
    if (conversationId) form.set('conversationId', conversationId)
    for (const file of attachments) form.append('files', file, file.name)

    const response = await fetch(`${requireBaseUrl()}/api/v1/agent/chat/${hasAttachments ? 'attachments/stream' : 'stream'}`, {
      method: 'POST',
      headers: headers(hasAttachments ? {} : { 'Content-Type': 'application/json' }),
      body: hasAttachments ? form : JSON.stringify({
        message,
        context: { ...(agentId ? { agentId } : {}), ...(conversationId ? { conversationId } : {}) },
      }),
    })
    if (!response.ok) throw await readError(response)
    if (!response.body) throw new Error('Engine 未返回流式响应')
    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    try {
      while (true) {
        const { done, value } = await reader.read()
        buffer += decoder.decode(value || new Uint8Array(), { stream: !done })
        const blocks = buffer.split(/\r?\n\r?\n/)
        buffer = blocks.pop() || ''
        for (const block of blocks) {
          const event = parseSseBlock(block)
          if (event) yield event
        }
        if (done) break
      }
      const event = parseSseBlock(buffer)
      if (event) yield event
    } finally {
      reader.releaseLock()
    }
  },
}

export function makeLocalConversation(agentId: string, message: string): ConversationRecord {
  const now = new Date().toISOString()
  const userMessage: ConversationMessage = {
    messageId: crypto.randomUUID(),
    sequence: 1,
    role: 'user',
    content: message,
    timestamp: now,
  }
  return {
    conversationId: crypto.randomUUID(),
    tenantId: getTenantId(),
    userId: 'local',
    agentId,
    status: 'Running',
    createdAt: now,
    updatedAt: now,
    lastMessageAt: now,
    messageCount: 1,
    title: message.slice(0, 40),
    messages: [userMessage],
  }
}
