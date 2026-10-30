import { beforeEach, describe, expect, it, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
  vi.unstubAllGlobals()
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

    expect(fetchMock).toHaveBeenCalledWith('/runtime-config.json', { cache: 'no-store' })
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

  it('keeps local development defaults with the shipped empty configuration', async () => {
    vi.stubGlobal('localStorage', { getItem: () => null })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}')))
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    await loadRuntimeConfig()
    const http = await import('./shared/api/http')
    expect(http.getRouterBaseUrl()).toBe('http://localhost:5001')
    expect(http.getEngineBaseUrl()).toBe('http://localhost:5208')
    expect(http.getTenantId()).toBe('development')
  })

  it.each([
    null,
    [],
    { routerBaseUrl: 5001 },
    { routerBaseUrl: 'javascript:alert(1)' },
    { engineBaseUrl: '/relative' },
    { engineBaseUrl: 'https://user:password@example.com' },
    { engineBaseUrl: 'https://example.com?token=secret' },
    { tenantId: false },
  ])('rejects invalid deployment settings: %j', async (config) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(config))))
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    await expect(loadRuntimeConfig()).rejects.toThrow()
  })

  it.each([
    () => Promise.resolve(new Response('', { status: 404 })),
    () => Promise.resolve(new Response('<html>Fallback page</html>')),
    () => Promise.reject(new TypeError('Network unavailable')),
  ])('reports configuration failures instead of silently using local endpoints', async (fetchConfig) => {
    vi.stubGlobal('fetch', vi.fn(fetchConfig))
    const { loadRuntimeConfig } = await import('./shared/runtimeConfig')
    await expect(loadRuntimeConfig()).rejects.toThrow()
  })
})
