import { beforeEach, describe, expect, it, vi } from 'vitest'

/**
 * The audit console has no user list: it resolves whoever the token belongs to
 * by asking OpenAgent. So "open the audit console" is nothing more than
 * carrying the workspace's existing login over to it — which is what these
 * tests pin down.
 */

class MemoryStorage {
  private store = new Map<string, string>()
  getItem(key: string) { return this.store.get(key) ?? null }
  setItem(key: string, value: string) { this.store.set(key, String(value)) }
  removeItem(key: string) { this.store.delete(key) }
  clear() { this.store.clear() }
}

beforeEach(() => {
  vi.stubGlobal('localStorage', new MemoryStorage())
  vi.stubGlobal('sessionStorage', new MemoryStorage())
  localStorage.setItem('openagent.router.base-url', 'http://router.example:5001')
})

const load = async () => {
  vi.resetModules()
  return await import('./useAuditSettings')
}

describe('audit base url', () => {
  it('defaults to port 8100 on the workspace host', async () => {
    const { defaultAuditBaseUrl } = await load()
    // jsdom origin is http://localhost:3000 in this suite
    expect(defaultAuditBaseUrl).toMatch(/^http:\/\/localhost:8100$|^http:\/\/localhost$/)
  })

  it('normalizes to an origin, dropping paths and trailing slashes', async () => {
    const { normalizeAuditBaseUrl } = await load()
    expect(normalizeAuditBaseUrl('http://10.51.4.45:8100/')).toBe('http://10.51.4.45:8100')
    expect(normalizeAuditBaseUrl('http://10.51.4.45:8100/ui/login')).toBe('http://10.51.4.45:8100')
    expect(normalizeAuditBaseUrl('  https://audit.example.com  ')).toBe('https://audit.example.com')
  })

  it('rejects values that are not http(s)', async () => {
    const { normalizeAuditBaseUrl } = await load()
    expect(normalizeAuditBaseUrl('javascript:alert(1)')).toBe('')
    expect(normalizeAuditBaseUrl('file:///etc/passwd')).toBe('')
    expect(normalizeAuditBaseUrl('not a url')).toBe('')
    expect(normalizeAuditBaseUrl('')).toBe('')
  })

  it('persists a saved address and reads it back', async () => {
    const { useAuditSettings, readAuditBaseUrl } = await load()
    const { saveAuditBaseUrl } = useAuditSettings()

    expect(saveAuditBaseUrl('http://10.51.4.45:8100/')).toBe('http://10.51.4.45:8100')
    expect(readAuditBaseUrl()).toBe('http://10.51.4.45:8100')
  })

  it('falls back to the default when the saved value is unusable', async () => {
    const { readAuditBaseUrl, defaultAuditBaseUrl } = await load()
    localStorage.setItem('openagent.audit.base-url', 'garbage')
    expect(readAuditBaseUrl()).toBe(defaultAuditBaseUrl)
  })

  it('reset restores and clears the stored value', async () => {
    const { useAuditSettings, readAuditBaseUrl, defaultAuditBaseUrl } = await load()
    const { saveAuditBaseUrl, resetAuditBaseUrl } = useAuditSettings()

    saveAuditBaseUrl('http://custom:9999')
    expect(resetAuditBaseUrl()).toBe(defaultAuditBaseUrl)
    expect(readAuditBaseUrl()).toBe(defaultAuditBaseUrl)
    expect(localStorage.getItem('openagent.audit.base-url')).toBeNull()
  })
})

describe('identity hand-over', () => {
  it('reads the token the workspace already holds', async () => {
    const http = await import('../../shared/api/http')
    // Simulate a completed workspace login.
    http.setAccessToken('signed-token', 'Bearer')

    expect(http.getAccessToken()).toBe('signed-token')
    expect(http.getTokenType()).toBe('Bearer')
  })

  it('reports no token when the workspace is not signed in', async () => {
    const http = await import('../../shared/api/http')
    http.clearAuthentication()
    sessionStorage.removeItem('openagent.auth.access-token')
    expect(Boolean(http.getAccessToken())).toBe(false)
  })

  it('builds the console login url the server expects', async () => {
    const http = await import('../../shared/api/http')
    http.setAccessToken('signed-token', 'Bearer')

    const origin = 'http://10.51.4.45:8100'
    const target = new URL('/ui/login', origin)
    target.searchParams.set('token', http.getAccessToken())

    expect(target.pathname).toBe('/ui/login')
    expect(target.searchParams.get('token')).toBe('signed-token')
    expect(target.origin).toBe(origin)
  })

  it('keeps the token out of localStorage', async () => {
    const http = await import('../../shared/api/http')
    http.setAccessToken('signed-token', 'Bearer')

    // The console copies the token into a URL; it must not additionally start
    // persisting it in a place that outlives the tab.
    expect(localStorage.getItem('openagent.auth.access-token')).toBeNull()
    expect(sessionStorage.getItem('openagent.auth.access-token')).toBe('signed-token')
  })
})