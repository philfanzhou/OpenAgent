import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, AUTH_FAILURE_EVENT, clearAuthentication, fetchHealthReport, getAccessToken, getEngineBaseUrl, getRouterBaseUrl, getTenantId, setAccessToken, setConnectionMode, setEngineBaseUrl, setRouterBaseUrl, setTenantId } from './api'

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>()

  get length(): number { return this.values.size }
  clear(): void { this.values.clear() }
  getItem(key: string): string | null { return this.values.get(key) ?? null }
  key(index: number): string | null { return Array.from(this.values.keys())[index] ?? null }
  removeItem(key: string): void { this.values.delete(key) }
  setItem(key: string, value: string): void { this.values.set(key, value) }
}

beforeEach(() => {
  vi.unstubAllGlobals()
  vi.stubGlobal('localStorage', new MemoryStorage())
  vi.stubGlobal('sessionStorage', new MemoryStorage())
  vi.restoreAllMocks()
  vi.useRealTimers()
})

describe('workspace API', () => {
  it('uses runnable local stack defaults before the user customizes a connection', () => {
    expect(getRouterBaseUrl()).toBe('http://localhost:5001')
    expect(getEngineBaseUrl()).toBe('http://localhost:5208')
    expect(getTenantId()).toBe('development')
  })

  it('sends only authentication credentials to catalog requests', async () => {
    setConnectionMode('router')
    setRouterBaseUrl('http://router.example/')
    setAccessToken('encoded-user', 'Basic')
    setTenantId('tenant-1')
    const fetchMock = vi.fn().mockResolvedValue(new Response('[]', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    await api.listAgents()

    expect(fetchMock).toHaveBeenCalledOnce()
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('http://router.example/api/v1/agent/agents')
    const requestHeaders = init.headers as Headers
    expect(requestHeaders.get('Authorization')).toBe('Basic encoded-user')
    expect(requestHeaders.has('X-Tenant-Id')).toBe(false)
    expect(requestHeaders.get('X-Trace-Id')).toBeTruthy()
  })

  it('keeps tokens out of localStorage and binds them to the authenticated endpoint', () => {
    setConnectionMode('router')
    setRouterBaseUrl('https://router.example')
    setAccessToken('session-only-token', 'Bearer', 60)

    expect(getAccessToken()).toBe('session-only-token')
    expect(localStorage.getItem('openagent.auth.access-token')).toBeNull()

    setRouterBaseUrl('https://other-router.example')
    expect(getAccessToken()).toBe('')
  })

  it('drops expired session tokens', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-08-17T00:00:00Z'))
    setAccessToken('short-lived-token', 'Bearer', 1)
    vi.setSystemTime(new Date('2026-08-17T00:00:02Z'))

    expect(getAccessToken()).toBe('')
    expect(sessionStorage.getItem('openagent.auth.access-token')).toBeNull()
  })

  it('does not send a client tenant header with bearer tokens', async () => {
    setConnectionMode('router')
    setRouterBaseUrl('https://router.example')
    setTenantId('stale-client-tenant')
    setAccessToken('signed-token', 'Bearer')
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    await api.getCurrentUser()

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    const requestHeaders = init.headers as Headers
    expect(requestHeaders.get('Authorization')).toBe('Bearer signed-token')
    expect(requestHeaders.has('X-Tenant-Id')).toBe(false)
  })

  it.each([401, 403])('emits auth failure status %s without exposing a token from error details', async (status) => {
    setConnectionMode('router')
    setRouterBaseUrl('https://router.example')
    const dispatchEvent = vi.fn()
    class TestCustomEvent {
      constructor(public readonly type: string, public readonly init: { detail: unknown }) {}
    }
    vi.stubGlobal('window', { dispatchEvent })
    vi.stubGlobal('CustomEvent', TestCustomEvent)
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      detail: 'authorization=Bearer secret-value',
    }), { status, statusText: status === 401 ? 'Unauthorized' : 'Forbidden' })))

    const error = await api.getCurrentUser().catch(value => value) as ApiError

    expect(error.status).toBe(status)
    expect(error.message).not.toContain('secret-value')
    expect(dispatchEvent).toHaveBeenCalledOnce()
    expect(dispatchEvent.mock.calls[0]?.[0]).toMatchObject({ type: AUTH_FAILURE_EVENT, init: { detail: { status } } })
    clearAuthentication()
  })

  it('parses SSE events from streaming chat', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example/')
    const stream = [
      'event: reasoning',
      'data: {"content":"inspect first"}',
      '',
      'event: content',
      'data: {"content":"hello"}',
      '',
      'event: done',
      'data: {"done":true,"usage":{"promptTokens":21,"completionTokens":8,"totalTokens":29},"modelId":"provider-model"}',
      '',
    ].join('\n')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(stream, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream' },
    })))

    const events = []
    for await (const event of api.streamChat('hello', 'support')) events.push(event)

    expect(events).toEqual([
      { type: 'reasoning', content: 'inspect first' },
      { type: 'content', content: 'hello' },
      {
        type: 'done',
        done: true,
        usage: { promptTokens: 21, completionTokens: 8, totalTokens: 29 },
        modelId: 'provider-model',
      },
    ])
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe('http://engine.example/api/v1/agent/chat/stream')
  })

  it('emits the router-selected agent before reading the SSE body', async () => {
    setConnectionMode('router')
    setRouterBaseUrl('http://router.example')
    const stream = [
      'event: content',
      'data: {"content":"hello"}',
      '',
    ].join('\n')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(stream, {
      status: 200,
      headers: {
        'Content-Type': 'text/event-stream',
        'X-OpenAgent-Selected-Agent-Id': 'support',
      },
    })))

    const events = []
    for await (const event of api.streamChat('hello')) events.push(event)

    expect(events[0]).toEqual({ type: 'agent_selected', agentId: 'support' })
    expect(events[1]).toEqual({ type: 'content', content: 'hello' })
  })

  it('preserves streamed tool arguments and problem details for the message UI', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example/')
    const stream = [
      'event: tool_call',
      'data: {"toolName":"write_file","toolCallId":"call-1","toolArguments":{"path":"report.md"}}',
      '',
      'event: error',
      'data: {"title":"Execution failed","detail":"Tool unavailable","traceId":"trace-1"}',
      '',
    ].join('\n')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(stream, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream' },
    })))

    const events = []
    for await (const event of api.streamChat('create a report', 'support')) events.push(event)

    expect(events).toEqual([
      { type: 'tool_call', toolName: 'write_file', toolCallId: 'call-1', toolArguments: { path: 'report.md' } },
      { type: 'error', error: { title: 'Execution failed', detail: 'Tool unavailable', traceId: 'trace-1' } },
    ])
  })

  it('binds streaming fetch cancellation to the supplied request signal', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example/')
    const controller = new AbortController()
    const fetchMock = vi.fn((_url: string | URL | Request, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
    }))
    vi.stubGlobal('fetch', fetchMock)

    const nextEvent = api.streamChat('hello', 'support', 'conversation-a', [], 'conversation-a', controller.signal).next()
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledOnce())
    controller.abort()

    await expect(nextEvent).rejects.toMatchObject({ name: 'AbortError' })
    expect(fetchMock.mock.calls[0]?.[1]?.signal).toBe(controller.signal)
  })

  it('uploads chat files as multipart data', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example')
    const responseBody = {
      fileId: 'file-1', tenantId: 'development', ownerUserId: 'user-1', fileName: 'notes.md',
      mediaType: 'text/markdown', length: 12, sha256: 'abc', source: 'UserUpload', state: 'Ready', createdAt: '',
    }
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(responseBody), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await api.uploadFile(new File(['# Notes'], 'notes.md', { type: 'text/markdown' }))

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('http://engine.example/api/v1/agent/files')
    expect(init.body).toBeInstanceOf(FormData)
    expect(result.fileId).toBe('file-1')
  })

  it('includes gateway problem details and trace ID in errors', async () => {
    setConnectionMode('router')
    setRouterBaseUrl('http://router.example')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      detail: 'No Engine is available',
      traceId: 'trace-1',
    }), {
      status: 503,
      statusText: 'Service Unavailable',
      headers: { 'Content-Type': 'application/problem+json' },
    })))

    await expect(api.getCurrentUser()).rejects.toThrow('No Engine is available (TraceId: trace-1)')
  })

  it('uploads skill packages as multipart data to the selected agent', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example')
    const responseBody = {
      skill: { skillId: 'weather', name: 'weather', enabled: true, objectKey: 'skills/weather.zip', packageFormat: 'zip' },
      currentVersion: '2',
      storage: 'object-storage',
    }
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(responseBody), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    const file = new File(['official skill package'], 'skill.zip', { type: 'application/zip' })

    const result = await api.uploadSkillPackage('support', file)

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('http://engine.example/api/v1/admin/skills/support/packages')
    expect(init.body).toBeInstanceOf(FormData)
    expect(((init.body as FormData).get('file') as File).name).toBe(file.name)
    expect(result.storage).toBe('object-storage')
  })

  it('sends MCP and Skill bindings inside the Agent configuration', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      agentId: 'support',
      name: 'Support',
      config: {
        mcp: { servers: [] },
        skills: { enabledSkills: [], instances: [] },
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    await api.saveAgentConfig('support', {
      agentId: 'support',
      name: 'Support',
      description: '',
      status: 0,
      currentVersion: '1',
      config: {
        instructions: '',
        llm: { provider: '', format: 'OpenAIChatCompletions', modelId: 'gpt-4o', apiKey: '', endpoint: '', temperature: 0.7 },
        mcp: {
          servers: [{ name: 'tools', url: 'https://mcp.example.test/mcp', type: 'Http', protocolVersion: '2025-06-18' }],
        },
        rag: { enabled: false, enabledRagInstanceIds: [], instances: [] },
        skills: { enabledSkills: ['weather'], instances: [{ skillId: 'weather', name: 'weather', enabled: true, objectKey: 'skills/weather.zip', packageFormat: 'zip' }] },
        maxTurns: 50,
      },
    })

    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe('http://engine.example/api/v1/admin/agents/support/config')
    const init = fetchMock.mock.calls[0]?.[1] as RequestInit
    const body = JSON.parse(String(init.body)) as { config: { mcp: { servers: Array<{ protocolVersion: string }>; }; skills: { enabledSkills: string[] } } }
    expect(body.config.mcp.servers[0].protocolVersion).toBe('2025-06-18')
    expect(body.config.skills.enabledSkills).toEqual(['weather'])
  })

  it('reads execution policies and explicitly authorizes one Skill script', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example')
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({
        stdioEnabled: false,
        stdioIsolation: 'disabled',
        allowedCommands: [],
        protocolVersionPolicy: 'automatic-or-minimum',
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        enabled: true,
        isolation: 'container-unix-socket',
        supportedExtensions: ['.py'],
        timeoutSeconds: 10,
        maxScriptBytes: 131072,
        maxOutputBytes: 65536,
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        skillId: 'analysis',
        name: 'analysis',
        enabled: true,
        scriptCount: 1,
        allowScriptExecution: true,
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)

    await api.getMcpRuntime()
    await api.getSkillRuntime()
    const updated = await api.setSkillScriptExecution('analysis', true)

    expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
      'http://engine.example/api/v1/admin/mcp/runtime',
      'http://engine.example/api/v1/admin/skills/runtime',
      'http://engine.example/api/v1/admin/skills/analysis/execution',
    ])
    expect(JSON.parse(String((fetchMock.mock.calls[2]?.[1] as RequestInit).body))).toEqual({ enabled: true })
    expect(updated.allowScriptExecution).toBe(true)
  })

  it('migrates the previous single endpoint to the Router address', () => {
    localStorage.setItem('openagent.engine.base-url', 'http://legacy-router.example')

    expect(getRouterBaseUrl()).toBe('http://legacy-router.example')
  })

  it('scopes file reads to the conversation id', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example/')
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('content', { status: 200 })))

    await expect(api.readFileText('file/1', 'conversation/1')).resolves.toBe('content')

    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(
      'http://engine.example/api/v1/agent/files/file%2F1/content?conversationId=conversation%2F1',
    )
  })

  it('manually triggers conversation compaction with an encoded conversation id', async () => {
    setConnectionMode('engine')
    setEngineBaseUrl('http://engine.example/')
    const responseBody = {
      compressionId: 'compression-1',
      strategy: 'truncation',
      trigger: 'Manual',
      status: 'Succeeded',
      lastCompressedAt: '2026-08-19T00:00:00Z',
      compressedMessageCount: 4,
      originalStartSequence: 1,
      originalEndSequence: 4,
      originalTokenCount: 40,
      tokenCount: 20,
      originalHistoryRestored: false,
      sourceEndSequence: 4,
    }
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(responseBody), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await api.compactConversation('conversation/1')

    expect(result.trigger).toBe('Manual')
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      'http://engine.example/api/v1/agent/conversations/conversation%2F1/compact',
    )
    expect((fetchMock.mock.calls[0]?.[1] as RequestInit).method).toBe('POST')
  })

  it('parses the engine health report into typed items', async () => {
    const report = {
      status: 'Healthy',
      service: 'agent-engine',
      totalDurationMs: 8,
      items: [
        { key: 'redis', status: 'Healthy', detail: 'Redis connection is healthy', latencyMs: 2, data: {} },
        { key: 'database', status: 'Healthy', detail: 'Database is reachable', latencyMs: 4, data: {} },
      ],
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(report), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })))

    const result = await fetchHealthReport('http://engine.example/')

    expect(result.status).toBe('Healthy')
    expect(result.items).toHaveLength(2)
    expect(result.items[0].key).toBe('redis')
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe('http://engine.example/health/report')
  })
})
