import type {
  AgentConfigEntity,
  AgentSummary,
  AuthConfig,
  AuthTokenResponse,
  ConversationMessage,
  ConversationRecord,
  ContextSummary,
  ConnectionMode,
  CurrentUserContext,
  FileAsset,
  HealthEntry,
  HealthReport,
  HealthReportItem,
  LlmProviderProfile,
  LlmTestResult,
  MessageFile,
  McpServerConfig,
  McpTestResult,
  NativeHealthReport,
  RagConfig,
  RagInstanceConfig,
  RagTestResult,
  SkillPackageInstallResponse,
  SkillCatalogItem,
  SkillInstanceConfig,
  SkillTestResult,
  StreamEvent,
} from './types'

const legacyBaseUrlStorageKey = 'openagent.engine.base-url'
const routerStorageKey = 'openagent.router.base-url'
const engineStorageKey = 'openagent.direct-engine.base-url'
const connectionModeStorageKey = 'openagent.connection.mode'
const tokenStorageKey = 'openagent.auth.access-token'
const tokenTypeStorageKey = 'openagent.auth.token-type'
const tokenExpiryStorageKey = 'openagent.auth.expires-at'
const tokenEndpointStorageKey = 'openagent.auth.endpoint'
const tenantStorageKey = 'openagent.auth.tenant-id'
export const AUTH_FAILURE_EVENT = 'openagent:auth-failure'
const defaultRouterBaseUrl = import.meta.env.VITE_OPENAGENT_ROUTER_BASE_URL || 'http://localhost:5001'
const defaultEngineBaseUrl = import.meta.env.VITE_OPENAGENT_ENGINE_BASE_URL || 'http://localhost:5208'
const defaultTenantId = import.meta.env.VITE_OPENAGENT_TENANT_ID || 'development'

function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/$/, '')
}

export function getConnectionMode(): ConnectionMode {
  return localStorage.getItem(connectionModeStorageKey) === 'engine' ? 'engine' : 'router'
}

export function setConnectionMode(value: ConnectionMode): void {
  localStorage.setItem(connectionModeStorageKey, value)
}

export function getRouterBaseUrl(): string {
  return localStorage.getItem(routerStorageKey)
    || localStorage.getItem(legacyBaseUrlStorageKey)
    || defaultRouterBaseUrl
}

export function setRouterBaseUrl(value: string): void {
  localStorage.setItem(routerStorageKey, normalizeBaseUrl(value))
  localStorage.removeItem(legacyBaseUrlStorageKey)
}

export function getEngineBaseUrl(): string {
  return localStorage.getItem(engineStorageKey) || defaultEngineBaseUrl
}

export function setEngineBaseUrl(value: string): void {
  localStorage.setItem(engineStorageKey, normalizeBaseUrl(value))
}

export function getAccessToken(): string {
  const expiresAt = Number(sessionStorage.getItem(tokenExpiryStorageKey) || 0)
  if (expiresAt > 0 && Date.now() >= expiresAt) {
    clearAuthentication()
    return ''
  }
  const tokenEndpoint = sessionStorage.getItem(tokenEndpointStorageKey)
  if (tokenEndpoint && tokenEndpoint !== normalizeBaseUrl(requireBaseUrl())) return ''
  return sessionStorage.getItem(tokenStorageKey) || ''
}

export function getTokenType(): string {
  return sessionStorage.getItem(tokenTypeStorageKey) || ''
}

export function setAccessToken(value: string, tokenType = 'Basic', expiresIn?: number): void {
  if (value.trim()) {
    sessionStorage.setItem(tokenStorageKey, value.trim())
    sessionStorage.setItem(tokenTypeStorageKey, tokenType.trim() || 'Basic')
    sessionStorage.setItem(tokenEndpointStorageKey, normalizeBaseUrl(requireBaseUrl()))
    if (expiresIn && Number.isFinite(expiresIn) && expiresIn > 0) {
      sessionStorage.setItem(tokenExpiryStorageKey, String(Date.now() + expiresIn * 1000))
    } else {
      sessionStorage.removeItem(tokenExpiryStorageKey)
    }
  } else {
    clearAuthentication()
  }
}

export function clearAuthentication(): void {
  sessionStorage.removeItem(tokenStorageKey)
  sessionStorage.removeItem(tokenTypeStorageKey)
  sessionStorage.removeItem(tokenExpiryStorageKey)
  sessionStorage.removeItem(tokenEndpointStorageKey)
}

export function getTenantId(): string {
  return localStorage.getItem(tenantStorageKey) || defaultTenantId
}

export function setTenantId(value: string): void {
  localStorage.setItem(tenantStorageKey, value.trim())
}

function requireBaseUrl(): string {
  const mode = getConnectionMode()
  const value = mode === 'router' ? getRouterBaseUrl() : getEngineBaseUrl()
  if (!value) throw new Error(`请先在设置中输入 ${mode === 'router' ? 'Router' : 'Engine'} 地址`)
  return value
}

function headers(extra: HeadersInit = {}): Headers {
  const result = new Headers({
    Accept: 'application/json',
    ...extra,
  })
  const token = getAccessToken()
  const tokenType = getTokenType() || 'Basic'
  if (token) result.set('Authorization', `${tokenType} ${token}`)
  result.set('X-Trace-Id', crypto.randomUUID())
  return result
}

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message)
    this.name = 'ApiError'
  }
}

function safeErrorMessage(value: string, fallback: string): string {
  const trimmed = value.trim().slice(0, 500)
  if (!trimmed) return fallback
  return trimmed
    .replace(/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/g, '[redacted token]')
    .replace(/\b(Basic|Bearer)\s+[A-Za-z0-9._~+\/-]+=*/gi, '$1 [redacted]')
    .replace(/(access_token|refresh_token|authorization|password)\s*[=:]\s*[^\s,;]+/gi, '$1=[redacted]')
}

function notifyAuthenticationFailure(status: number): void {
  if ((status === 401 || status === 403) && typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent(AUTH_FAILURE_EVENT, { detail: { status } }))
  }
}

async function readError(response: Response): Promise<ApiError> {
  const fallback = `${response.status} ${response.statusText || '请求失败'}`
  const raw = await response.text()
  notifyAuthenticationFailure(response.status)
  if (!raw.trim()) return new ApiError(fallback, response.status)

  try {
    const body = JSON.parse(raw) as {
      detail?: string
      title?: string
      message?: string
      error?: string | { detail?: string; message?: string }
      traceId?: string
      trace_id?: string
    }
    const nestedError = typeof body.error === 'string' ? body.error : body.error?.detail || body.error?.message
    const message = safeErrorMessage(body.detail || body.message || nestedError || body.title || fallback, fallback)
    const rawTraceId = body.traceId || body.trace_id
    const traceId = rawTraceId ? safeErrorMessage(rawTraceId, '') : ''
    return new ApiError(`${message}${traceId ? ` (TraceId: ${traceId})` : ''}`, response.status)
  } catch {
    return new ApiError(fallback, response.status)
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

export async function fetchHealthReport(baseUrl: string): Promise<HealthReport> {
  const response = await fetch(`${normalizeBaseUrl(baseUrl)}/health/report`, { headers: headers() })
  if (!response.ok) throw await readError(response)
  return await response.json() as HealthReport
}

export async function fetchHealth(baseUrl: string, path: '/health' | '/ready'): Promise<NativeHealthReport> {
  const response = await fetch(`${normalizeBaseUrl(baseUrl)}${path}`, { headers: headers() })
  if (!response.ok) throw await readError(response)
  // Router 的健康端点以纯文本返回 "Healthy"/"Degraded"/"Unhealthy"，
  // Engine 返回 JSON HealthReport；统一做兜底解析。
  const text = await response.text()
  try {
    return JSON.parse(text) as NativeHealthReport
  } catch {
    return { status: text.trim(), entries: {} } as NativeHealthReport
  }
}

function normalizeConversation(record: ConversationRecord): ConversationRecord {
  return {
    ...record,
    messages: record.messages?.map(message => {
      const raw = message.metadata?.Files
      const reasoning = message.metadata?.Reasoning
      if (!raw) return reasoning ? { ...message, reasoning } : message
      try {
        const files = (JSON.parse(raw) as Record<string, unknown>[]).map(file => ({
          fileId: String(file.fileId ?? file.FileId ?? ''),
          fileName: String(file.fileName ?? file.FileName ?? ''),
          mediaType: String(file.mediaType ?? file.MediaType ?? 'application/octet-stream'),
          length: Number(file.length ?? file.Length ?? 0),
          ...(file.objectKey || file.ObjectKey
            ? { objectKey: String(file.objectKey ?? file.ObjectKey) }
            : {}),
        })) satisfies MessageFile[]
        return { ...message, files, ...(reasoning ? { reasoning } : {}) }
      } catch {
        return reasoning ? { ...message, reasoning } : message
      }
    }),
  }
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
    return request<ConversationRecord>(`/api/v1/agent/conversations/${encodeURIComponent(id)}`).then(normalizeConversation)
  },

  async uploadFile(file: File): Promise<FileAsset> {
    const form = new FormData()
    form.set('file', file, file.name)
    const response = await fetch(`${requireBaseUrl()}/api/v1/agent/files`, {
      method: 'POST',
      headers: headers(),
      body: form,
    })
    if (!response.ok) throw await readError(response)
    return await response.json() as FileAsset
  },

  async loadFilePreview(fileId: string, conversationId: string): Promise<string> {
    const response = await fetch(
      `${requireBaseUrl()}/api/v1/agent/files/${encodeURIComponent(fileId)}/content?conversationId=${encodeURIComponent(conversationId)}`,
      { headers: headers() },
    )
    if (!response.ok) throw await readError(response)
    return URL.createObjectURL(await response.blob())
  },

  async loadObjectPreview(objectKey: string, conversationId?: string): Promise<string> {
    const query = new URLSearchParams({ path: objectKey })
    if (conversationId) query.set('conversationId', conversationId)
    const response = await fetch(
      `${requireBaseUrl()}/api/v1/agent/files/object?${query.toString()}`,
      { headers: headers() },
    )
    if (!response.ok) throw await readError(response)
    return URL.createObjectURL(await response.blob())
  },

  async readFileText(fileId: string, conversationId: string): Promise<string> {
    const response = await fetch(
      `${requireBaseUrl()}/api/v1/agent/files/${encodeURIComponent(fileId)}/content?conversationId=${encodeURIComponent(conversationId)}`,
      { headers: headers() },
    )
    if (!response.ok) throw await readError(response)
    return await response.text()
  },

  async downloadFile(fileId: string, fileName: string, conversationId: string): Promise<void> {
    const response = await fetch(
      `${requireBaseUrl()}/api/v1/agent/files/${encodeURIComponent(fileId)}/download?conversationId=${encodeURIComponent(conversationId)}`,
      { headers: headers() },
    )
    if (!response.ok) throw await readError(response)
    const url = URL.createObjectURL(await response.blob())
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = fileName
    anchor.click()
    URL.revokeObjectURL(url)
  },

  deleteConversation(id: string): Promise<void> {
    return request<void>(`/api/v1/agent/conversations/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },

  compactConversation(id: string): Promise<ContextSummary> {
    return request<ContextSummary>(`/api/v1/agent/conversations/${encodeURIComponent(id)}/compact`, { method: 'POST' })
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

  async uploadSkillPackage(agentId: string, file: File): Promise<SkillPackageInstallResponse> {
    const form = new FormData()
    form.set('file', file, file.name)
    const response = await fetch(`${requireBaseUrl()}/api/v1/admin/skills/${encodeURIComponent(agentId)}/packages`, {
      method: 'POST',
      headers: headers(),
      body: form,
    })
    if (!response.ok) throw await readError(response)
    return response.json() as Promise<SkillPackageInstallResponse>
  },

  async uploadSkillCatalog(file: File): Promise<{ skill: SkillInstanceConfig; storage: string }> {
    const form = new FormData()
    form.set('file', file, file.name)
    const response = await fetch(`${requireBaseUrl()}/api/v1/admin/skills/packages`, {
      method: 'POST',
      headers: headers(),
      body: form,
    })
    if (!response.ok) throw await readError(response)
    return response.json() as Promise<{ skill: SkillInstanceConfig; storage: string }>
  },

  deleteSkillCatalog(skillId: string): Promise<void> {
    return request<void>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}`, { method: 'DELETE' })
  },

  listSkills(): Promise<SkillCatalogItem[]> {
    return request<SkillCatalogItem[]>('/api/v1/admin/skills')
  },

  getSkill(skillId: string): Promise<SkillCatalogItem> {
    return request<SkillCatalogItem>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}`)
  },

  getSkillSource(skillId: string): Promise<{ markdown: string }> {
    return request<{ markdown: string }>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}/source`)
  },

  deleteSkillPackage(agentId: string, skillId: string): Promise<void> {
    return request<void>(`/api/v1/admin/skills/${encodeURIComponent(agentId)}/${encodeURIComponent(skillId)}`, { method: 'DELETE' })
  },

  testSkills(skills: AgentConfigEntity['config']['skills']): Promise<SkillTestResult> {
    return request<SkillTestResult>('/api/v1/admin/skills/test', {
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

  listMcpProfiles(): Promise<McpServerConfig[]> {
    return request<McpServerConfig[]>('/api/v1/admin/mcp')
  },

  getMcpProfile(id: string): Promise<McpServerConfig> {
    return request<McpServerConfig>(`/api/v1/admin/mcp/${encodeURIComponent(id)}`)
  },

  saveMcpProfile(id: string, server: McpServerConfig): Promise<McpServerConfig> {
    return request<McpServerConfig>(`/api/v1/admin/mcp/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(server),
    })
  },

  deleteMcpProfile(id: string): Promise<void> {
    return request<void>(`/api/v1/admin/mcp/${encodeURIComponent(id)}`, { method: 'DELETE' })
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

  async *streamChat(
    message: string,
    agentId?: string,
    conversationId?: string,
    fileIds: string[] = [],
    routingConversationId?: string,
    signal?: AbortSignal,
  ): AsyncGenerator<StreamEvent> {
    const requestHeaders = headers({ 'Content-Type': 'application/json' })
    if (routingConversationId) requestHeaders.set('X-Conversation-Id', routingConversationId)
    const response = await fetch(`${requireBaseUrl()}/api/v1/agent/chat/stream`, {
      method: 'POST',
      headers: requestHeaders,
      body: JSON.stringify({
        message,
        fileIds,
        context: { ...(agentId ? { agentId } : {}), ...(conversationId ? { conversationId } : {}) },
      }),
      signal,
    })
    if (!response.ok) throw await readError(response)
    if (!response.body) throw new Error('Engine 未返回流式响应')
    const selectedAgentId = response.headers.get('X-OpenAgent-Selected-Agent-Id')
    if (selectedAgentId) yield { type: 'agent_selected', agentId: selectedAgentId }
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
