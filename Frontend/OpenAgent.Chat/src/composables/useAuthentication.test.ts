import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { getAccessToken, setAccessToken, setRouterBaseUrl } from '../shared/api/http'
import { useAuthentication } from './useAuthentication'
import type { AuthConfig, CurrentUserContext } from '../shared/contracts/types'

vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn() } }))

const config: AuthConfig = {
  mode: 'Basic', development: true, password: { enabled: true, endpoint: '/api/v1/auth/password/token' },
  anonymous: { enabled: false },
  oidc: { authority: '', clientId: '', audience: '', scopes: [] },
}

function storage(): Storage {
  const values = new Map<string, string>()
  return {
    get length() { return values.size }, clear: () => values.clear(),
    getItem: key => values.get(key) ?? null, key: index => [...values.keys()][index] ?? null,
    setItem: (key, value) => { values.set(key, value) }, removeItem: key => { values.delete(key) },
  }
}

function authentication() {
  const options = {
    currentUser: ref<CurrentUserContext | null>(null), loadWorkspace: vi.fn().mockResolvedValue(undefined),
    resetWorkspace: vi.fn(), cancelStreams: vi.fn().mockResolvedValue(undefined),
    closeSettings: vi.fn(), notifyError: vi.fn(),
  }
  return { state: useAuthentication(options), options }
}

beforeEach(() => {
  vi.stubGlobal('localStorage', storage())
  vi.stubGlobal('sessionStorage', storage())
  vi.stubGlobal('document', { title: 'OpenAgent' })
  const location = { pathname: '/', hash: '', search: '' }
  vi.stubGlobal('window', {
    location, addEventListener: vi.fn(), removeEventListener: vi.fn(),
    history: { replaceState: vi.fn((_data: unknown, _title: string, value: string) => {
      const url = new URL(value, 'https://chat.example')
      location.hash = url.hash
      location.search = url.search
    }) },
  })
})

afterEach(() => vi.unstubAllGlobals())

describe('connection startup', () => {
  it('shows connection setup without making backend requests on first visit', async () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const { state } = authentication()
    await state.initializeAuthentication()
    expect(state.authView.value).toBe('login')
    expect(state.authError.value).toBe('')
    expect(state.routerUrl.value).toBe('')
    expect(state.engineUrl.value).toBe('')
    expect(fetchMock).not.toHaveBeenCalled()
    state.disposeAuthentication()
  })

  it.each(['router', 'engine'] as const)('saves only the selected %s address before detecting authentication', async mode => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(config)))
    vi.stubGlobal('fetch', fetchMock)
    const { state } = authentication()
    await state.updateLoginConnection({
      mode, routerUrl: mode === 'router' ? ' https://remote.example/gateway/// ' : '',
      engineUrl: mode === 'engine' ? ' https://remote.example/gateway/// ' : '', tenantId: '',
    })
    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('https://remote.example/gateway/api/v1/auth/config', expect.anything())
    expect(state.authConfig.value).toEqual(config)
    expect(state.authView.value).toBe('login')
    expect(localStorage.getItem(`openagent.${mode === 'router' ? 'router' : 'direct-engine'}.base-url`)).toBe('https://remote.example/gateway')
  })

  it.each(['', '/relative', 'https:/example.com', 'javascript:alert(1)', 'https://user:password@example.com', 'https://api.example?token=secret'])(
    'keeps invalid connection %s out of storage and sends no requests', async address => {
      const fetchMock = vi.fn()
      vi.stubGlobal('fetch', fetchMock)
      const { state } = authentication()
      await state.updateLoginConnection({ mode: 'router', routerUrl: address, engineUrl: '', tenantId: '' })
      expect(fetchMock).not.toHaveBeenCalled()
      expect(localStorage.getItem('openagent.router.base-url')).toBeNull()
      expect(state.authError.value).not.toBe('')
    },
  )

  it('reuses the saved connection on reload', async () => {
    setRouterBaseUrl('https://saved.example')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(config)))
    vi.stubGlobal('fetch', fetchMock)
    const { state } = authentication()
    await state.initializeAuthentication()
    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('https://saved.example/api/v1/auth/config', expect.anything())
    expect(state.authView.value).toBe('login')
    state.disposeAuthentication()
  })

  it('clears the previous session before contacting a different backend', async () => {
    setRouterBaseUrl('https://previous.example')
    setAccessToken('previous-token', 'Bearer')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(config)))
    vi.stubGlobal('fetch', fetchMock)
    const { state, options } = authentication()
    await state.updateLoginConnection({ mode: 'router', routerUrl: 'https://next.example', engineUrl: '', tenantId: '' })
    expect(getAccessToken()).toBe('')
    expect(options.resetWorkspace).toHaveBeenCalled()
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(new Headers(init.headers).has('Authorization')).toBe(false)
    expect(options.loadWorkspace).not.toHaveBeenCalled()
  })

  it('keeps the connection page available after a backend cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Network unavailable')))
    const { state, options } = authentication()
    await state.updateLoginConnection({ mode: 'engine', routerUrl: '', engineUrl: 'https://unavailable.example', tenantId: '' })
    expect(state.authView.value).toBe('login')
    expect(state.authConfig.value).toBeNull()
    expect(state.authError.value).toContain('检查连接地址')
    expect(options.loadWorkspace).not.toHaveBeenCalled()
  })

  it('allows login after connecting and loads the workspace only after authenticated identity is verified', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(config)))
      .mockResolvedValueOnce(new Response(JSON.stringify({ access_token: 'new-token', token_type: 'Basic' })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ isAuthenticated: true, userId: 'admin', tenantId: 'tenant-1' })))
    vi.stubGlobal('fetch', fetchMock)
    const { state, options } = authentication()
    await state.updateLoginConnection({ mode: 'router', routerUrl: 'https://remote.example', engineUrl: '', tenantId: '' })
    expect(options.loadWorkspace).not.toHaveBeenCalled()
    await state.loginWithPassword({ username: 'admin', password: 'admin' })
    expect(state.authView.value).toBe('workspace')
    expect(state.tenantId.value).toBe('tenant-1')
    expect(options.loadWorkspace).toHaveBeenCalledOnce()
    expect(localStorage.getItem('openagent.auth.access-token')).toBeNull()
  })
})
