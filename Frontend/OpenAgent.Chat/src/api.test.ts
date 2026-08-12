import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, getRouterBaseUrl, setAccessToken, setConnectionMode, setEngineBaseUrl, setRouterBaseUrl, setTenantId } from './api'

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

  it('migrates the previous single endpoint to the Router address', () => {
    localStorage.setItem('openagent.engine.base-url', 'http://legacy-router.example')

    expect(getRouterBaseUrl()).toBe('http://legacy-router.example')
  })
})
