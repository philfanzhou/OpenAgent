import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAccessToken, setConnectionMode, setRouterBaseUrl } from './api'
import { beginOidcLogin, clearAuthState, completeOidcLogin, sanitizeReturnHash, WORKSPACE_HASH } from './auth'
import type { AuthConfig } from './types'

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>()
  get length(): number { return this.values.size }
  clear(): void { this.values.clear() }
  getItem(key: string): string | null { return this.values.get(key) ?? null }
  key(index: number): string | null { return Array.from(this.values.keys())[index] ?? null }
  removeItem(key: string): void { this.values.delete(key) }
  setItem(key: string, value: string): void { this.values.set(key, value) }
}

const config: AuthConfig = {
  mode: 'JwtBearer',
  development: false,
  password: { enabled: false, endpoint: '/api/v1/auth/password/token' },
  anonymous: { enabled: false },
  oidc: {
    authority: 'https://idp.example',
    clientId: 'openagent-chat',
    audience: 'openagent-api',
    scopes: ['openid', 'profile'],
  },
}

beforeEach(() => {
  vi.unstubAllGlobals()
  vi.stubGlobal('localStorage', new MemoryStorage())
  vi.stubGlobal('sessionStorage', new MemoryStorage())
  setConnectionMode('router')
  setRouterBaseUrl('https://router.example')
  vi.restoreAllMocks()
})

describe('authentication flow', () => {
  it('rejects external and authentication return routes', () => {
    expect(sanitizeReturnHash('https://evil.example')).toBe(WORKSPACE_HASH)
    expect(sanitizeReturnHash('#/login')).toBe(WORKSPACE_HASH)
    expect(sanitizeReturnHash('#/auth/callback')).toBe(WORKSPACE_HASH)
    expect(sanitizeReturnHash('#/forbidden')).toBe(WORKSPACE_HASH)
    expect(sanitizeReturnHash('#/conversation/123')).toBe('#/conversation/123')
  })

  it('uses authorization code with PKCE and stores only the access token in sessionStorage', async () => {
    let assignedUrl = ''
    vi.stubGlobal('window', {
      location: {
        origin: 'https://chat.example',
        pathname: '/',
        assign: (value: string) => { assignedUrl = value },
      },
    })
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({
        authorization_endpoint: 'https://idp.example/authorize',
        token_endpoint: 'https://idp.example/token',
      }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        authorization_endpoint: 'https://idp.example/authorize',
        token_endpoint: 'https://idp.example/token',
      }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        access_token: 'verified-access-token',
        refresh_token: 'ignored-refresh-token',
        token_type: 'Bearer',
        expires_in: 300,
      }), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await beginOidcLogin(config, '#/conversation/123')
    const authorization = new URL(assignedUrl)
    expect(authorization.searchParams.get('response_type')).toBe('code')
    expect(authorization.searchParams.get('code_challenge_method')).toBe('S256')
    expect(authorization.searchParams.has('code_challenge')).toBe(true)
    expect(authorization.searchParams.has('client_secret')).toBe(false)

    const returnHash = await completeOidcLogin(config, new URLSearchParams({
      code: 'one-time-code',
      state: authorization.searchParams.get('state') || '',
    }))

    expect(returnHash).toBe('#/conversation/123')
    expect(getAccessToken()).toBe('verified-access-token')
    expect(localStorage.getItem('openagent.auth.access-token')).toBeNull()
    expect(sessionStorage.getItem('openagent.auth.refresh-token')).toBeNull()
    const tokenRequest = fetchMock.mock.calls[2]?.[1] as RequestInit
    expect(String(tokenRequest.body)).toContain('code_verifier=')
    expect(String(tokenRequest.body)).not.toContain('client_secret')
  })

  it('rejects an OIDC callback with the wrong state and clears transient values', async () => {
    let assignedUrl = ''
    vi.stubGlobal('window', {
      location: {
        origin: 'https://chat.example',
        pathname: '/',
        assign: (value: string) => { assignedUrl = value },
      },
    })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      authorization_endpoint: 'https://idp.example/authorize',
      token_endpoint: 'https://idp.example/token',
    }), { status: 200 })))
    await beginOidcLogin(config)

    await expect(completeOidcLogin(config, new URLSearchParams({ code: 'code', state: 'wrong' })))
      .rejects.toThrow('登录回调校验失败')
    expect(getAccessToken()).toBe('')
    expect(sessionStorage.getItem('openagent.auth.oidc-state')).toBeNull()
    expect(sessionStorage.getItem('openagent.auth.pkce-verifier')).toBeNull()
    expect(assignedUrl).toContain('code_challenge=')
  })

  it('clears tokens and transient login state on logout', () => {
    sessionStorage.setItem('openagent.auth.access-token', 'secret')
    sessionStorage.setItem('openagent.auth.oidc-state', 'state')
    sessionStorage.setItem('openagent.auth.pkce-verifier', 'verifier')
    clearAuthState()

    expect(sessionStorage.length).toBe(0)
  })
})
