import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, fetchHealthReport, getEngineBaseUrl, getRouterBaseUrl, getTenantId, setAccessToken, setConnectionMode, setEngineBaseUrl, setRouterBaseUrl, setTenantId } from './api'

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
  vi.stubGlobal('localStorage', new MemoryStorage())
  vi.stubGlobal('sessionStorage', new MemoryStorage())
  vi.restoreAllMocks()
})

describe('workspace API', () => {
  it('uses runnable local stack defaults before the user customizes a connection', () => {
    expect(getRouterBaseUrl()).toBe('http://localhost:5001')
    expect(getEngineBaseUrl()).toBe('http://localhost:5208')
    expect(getTenantId()).toBe('development')
  })

  it('sends gateway identity headers to catalog requests', async () => {
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
    expect(requestHeaders.get('X-Tenant-Id')).toBe('tenant-1')
    expect(requestHeaders.get('X-Trace-Id')).toBeTruthy()
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
      'data: {"done":true}',
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
      { type: 'done', done: true },
    ])
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe('http://engine.example/api/v1/agent/chat/stream')
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
