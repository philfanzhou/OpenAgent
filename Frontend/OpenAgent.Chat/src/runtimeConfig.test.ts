import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
  vi.unstubAllGlobals()
  vi.spyOn(console, 'warn').mockImplementation(() => {})
})

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('runtime configuration', () => {
  it('loads deployment settings before API clients use them, bypassing the browser cache', async () => {
    const saved = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => saved.get(key) ?? null,
      setItem: (key: string, value: string) => saved.set(key, value),
      removeItem: (key: string) => saved.delete(key),
    })
    const http = await import('./shared/api/http')
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      routerBaseUrl: ' https://router.example/gateway/ ',
      engineBaseUrl: 'https://engine.example/',
      tenantId: ' deployed-tenant ',
    })))
    vi.stubGlobal('fetch', fetchMock)

    await loadRuntimeConfig()

    expect(fetchMock).toHaveBeenCalledWith('/runtime-config.json', {
      cache: 'no-store', signal: expect.any(AbortSignal),
    })
    expect(http.getRouterBaseUrl()).toBe('https://router.example/gateway')
    expect(http.getEngineBaseUrl()).toBe('https://engine.example')
    expect(http.getTenantId()).toBe('deployed-tenant')

    http.setRouterBaseUrl('https://custom-router.example/')
    http.setEngineBaseUrl('https://custom-engine.example/')
    http.setTenantId('custom-tenant')
    expect(http.getRouterBaseUrl()).toBe('https://custom-router.example')
    expect(http.getEngineBaseUrl()).toBe('https://custom-engine.example')
    expect(http.getTenantId()).toBe('custom-tenant')

    http.setRouterBaseUrl('')
    http.setEngineBaseUrl('')
    http.setTenantId('')
    expect(http.getRouterBaseUrl()).toBe('https://router.example/gateway')
    expect(http.getEngineBaseUrl()).toBe('https://engine.example')
    expect(http.getTenantId()).toBe('deployed-tenant')
  })

  it('leaves backend addresses empty with the shipped empty configuration', async () => {
    vi.stubGlobal('localStorage', { getItem: () => null })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}')))
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    await loadRuntimeConfig()
    const http = await import('./shared/api/http')
    expect(http.getRouterBaseUrl()).toBe('')
    expect(http.getEngineBaseUrl()).toBe('')
    expect(http.getTenantId()).toBe('development')
  })

  it('selects direct Engine mode when it is the only deployment default and no mode is saved', async () => {
    vi.stubGlobal('localStorage', { getItem: () => null })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ engineBaseUrl: 'https://engine.example' }))))
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    await loadRuntimeConfig()
    const http = await import('./shared/api/http')
    expect(http.getConnectionMode()).toBe('engine')
    expect(http.getEngineBaseUrl()).toBe('https://engine.example')
    vi.stubGlobal('localStorage', { getItem: (key: string) => key === 'openagent.router.base-url' ? 'https://saved.example' : null })
    expect(http.getConnectionMode()).toBe('router')
    expect(http.getRouterBaseUrl()).toBe('https://saved.example')
  })

  it.each([
    null,
    [],
    { routerBaseUrl: 5001 },
    { routerBaseUrl: 'javascript:alert(1)' },
    { routerBaseUrl: 'https:/example.com' },
    { engineBaseUrl: '/relative' },
    { engineBaseUrl: 'https://user:password@example.com' },
    { engineBaseUrl: 'https://example.com?token=secret' },
    { tenantId: false },
  ])('ignores invalid deployment settings so users can configure a connection: %j', async (config) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(config))))
    const { loadRuntimeConfig, getRuntimeConfig } = await import('./shared/runtimeConfig')
    await loadRuntimeConfig()
    expect(getRuntimeConfig()).toEqual({})
  })

  it.each([
    () => Promise.resolve(new Response('', { status: 404 })),
    () => Promise.resolve(new Response('', { status: 500 })),
    () => Promise.resolve(new Response('<html>Fallback page</html>')),
    () => Promise.reject(new TypeError('Network unavailable')),
  ])('keeps saved connections usable when deployment configuration is unavailable', async (fetchConfig) => {
    vi.stubGlobal('localStorage', { getItem: (key: string) => key === 'openagent.router.base-url' ? 'https://saved.example' : null })
    vi.stubGlobal('fetch', vi.fn(fetchConfig))
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    await loadRuntimeConfig()
    const http = await import('./shared/api/http')
    expect(http.getRouterBaseUrl()).toBe('https://saved.example')
    expect(http.getEngineBaseUrl()).toBe('')
  })

  it('bounds the optional configuration request so a hung server cannot prevent startup', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn((_url: string, init: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
    })))
    const { loadRuntimeConfig, getRuntimeConfig } = await import('./shared/runtimeConfig')
    const loading = loadRuntimeConfig()
    await vi.advanceTimersByTimeAsync(3000)
    await loading
    expect(getRuntimeConfig()).toEqual({})
  })
})
