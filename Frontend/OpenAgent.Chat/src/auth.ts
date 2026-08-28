import { clearAuthentication, setAccessToken } from './api'
import type { AuthConfig } from './types'

const stateKey = 'openagent.auth.oidc-state'
const verifierKey = 'openagent.auth.pkce-verifier'
const returnHashKey = 'openagent.auth.return-hash'
const idTokenKey = 'openagent.auth.oidc-id-token'
const reauthenticationKey = 'openagent.auth.oidc-reauthentication-required'
export const LOGIN_HASH = '#/login'
export const WORKSPACE_HASH = '#/workspace'

interface OidcMetadata {
  authorization_endpoint: string
  token_endpoint: string
  end_session_endpoint?: string
}

interface OidcTokenResponse {
  access_token?: string
  id_token?: string
  token_type?: string
  expires_in?: number
}

function randomUrlSafe(bytes: number): string {
  const value = new Uint8Array(bytes)
  crypto.getRandomValues(value)
  return toBase64Url(value)
}

function toBase64Url(value: Uint8Array): string {
  let binary = ''
  for (const byte of value) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))
  return toBase64Url(new Uint8Array(digest))
}

function isSecureEndpoint(value: string): boolean {
  try {
    const url = new URL(value)
    return url.protocol === 'https:'
      || (url.protocol === 'http:' && (url.hostname === 'localhost' || url.hostname === '127.0.0.1'))
  } catch {
    return false
  }
}

export function sanitizeReturnHash(value?: string | null): string {
  if (!value || !value.startsWith('#/') || value === LOGIN_HASH || value === '#/forbidden' || value.startsWith('#/auth/')) {
    return WORKSPACE_HASH
  }
  return value
}

export function rememberReturnHash(value: string): void {
  sessionStorage.setItem(returnHashKey, sanitizeReturnHash(value))
}

export function takeReturnHash(): string {
  const value = sanitizeReturnHash(sessionStorage.getItem(returnHashKey))
  sessionStorage.removeItem(returnHashKey)
  return value
}

export function clearAuthState(): void {
  clearAuthentication()
  sessionStorage.removeItem(stateKey)
  sessionStorage.removeItem(verifierKey)
  sessionStorage.removeItem(returnHashKey)
  sessionStorage.removeItem(idTokenKey)
}

export function markReauthenticationRequired(): void {
  sessionStorage.setItem(reauthenticationKey, 'true')
}

export function cleanAuthorizationCallbackUrl(): URLSearchParams {
  const parameters = new URLSearchParams(window.location.search)
  window.history.replaceState(null, document.title, `${window.location.pathname}${LOGIN_HASH}`)
  return parameters
}

async function loadMetadata(authority: string): Promise<OidcMetadata> {
  const normalized = authority.replace(/\/$/, '')
  if (!isSecureEndpoint(normalized)) throw new Error('身份提供方必须使用 HTTPS')
  const response = await fetch(`${normalized}/.well-known/openid-configuration`, {
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    headers: { Accept: 'application/json' },
  })
  if (!response.ok) throw new Error('无法读取身份提供方配置')
  const metadata = await response.json() as Partial<OidcMetadata>
  if (!metadata.authorization_endpoint || !metadata.token_endpoint
    || !isSecureEndpoint(metadata.authorization_endpoint) || !isSecureEndpoint(metadata.token_endpoint)
    || (metadata.end_session_endpoint != null && !isSecureEndpoint(metadata.end_session_endpoint))) {
    throw new Error('身份提供方返回了不安全或不完整的配置')
  }
  return metadata as OidcMetadata
}

function redirectUri(): string {
  return `${window.location.origin}${window.location.pathname}`
}

export async function beginOidcLogin(config: AuthConfig, returnHash = WORKSPACE_HASH): Promise<void> {
  if (!config.oidc?.authority || !config.oidc.clientId) throw new Error('OIDC 登录尚未配置完成')
  const metadata = await loadMetadata(config.oidc.authority)
  const state = randomUrlSafe(32)
  const verifier = randomUrlSafe(64)
  sessionStorage.setItem(stateKey, state)
  sessionStorage.setItem(verifierKey, verifier)
  rememberReturnHash(returnHash)

  const url = new URL(metadata.authorization_endpoint)
  url.searchParams.set('client_id', config.oidc.clientId)
  url.searchParams.set('response_type', 'code')
  url.searchParams.set('redirect_uri', redirectUri())
  url.searchParams.set('scope', config.oidc.scopes.join(' '))
  url.searchParams.set('state', state)
  url.searchParams.set('code_challenge', await sha256(verifier))
  url.searchParams.set('code_challenge_method', 'S256')
  if (sessionStorage.getItem(reauthenticationKey) === 'true') {
    url.searchParams.set('prompt', 'login')
    sessionStorage.removeItem(reauthenticationKey)
  }
  window.location.assign(url.toString())
}

export async function buildOidcLogoutUrl(config: AuthConfig): Promise<string | null> {
  if (!config.oidc?.authority || !config.oidc.clientId) return null
  const metadata = await loadMetadata(config.oidc.authority)
  if (!metadata.end_session_endpoint) return null

  const url = new URL(metadata.end_session_endpoint)
  const idToken = sessionStorage.getItem(idTokenKey)
  if (idToken) url.searchParams.set('id_token_hint', idToken)
  url.searchParams.set('client_id', config.oidc.clientId)
  url.searchParams.set(
    'post_logout_redirect_uri',
    `${window.location.origin}${window.location.pathname}${LOGIN_HASH}`,
  )
  return url.toString()
}

export async function completeOidcLogin(
  config: AuthConfig,
  parameters: URLSearchParams,
): Promise<string> {
  const expectedState = sessionStorage.getItem(stateKey)
  const verifier = sessionStorage.getItem(verifierKey)
  const code = parameters.get('code')
  const state = parameters.get('state')
  const providerError = parameters.get('error')
  sessionStorage.removeItem(stateKey)
  sessionStorage.removeItem(verifierKey)

  if (providerError) throw new Error('身份提供方未完成登录')
  if (!code || !state || !expectedState || state !== expectedState || !verifier) {
    throw new Error('登录回调校验失败，请重新登录')
  }
  if (!config.oidc?.authority || !config.oidc.clientId) throw new Error('OIDC 登录尚未配置完成')

  const metadata = await loadMetadata(config.oidc.authority)
  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    client_id: config.oidc.clientId,
    code,
    redirect_uri: redirectUri(),
    code_verifier: verifier,
  })
  const response = await fetch(metadata.token_endpoint, {
    method: 'POST',
    credentials: 'omit',
    referrerPolicy: 'no-referrer',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    body,
  })
  if (!response.ok) throw new Error('身份提供方未能完成 token 交换')
  const result = await response.json() as OidcTokenResponse
  if (!result.access_token) throw new Error('身份提供方未返回访问 token')
  if (result.id_token) sessionStorage.setItem(idTokenKey, result.id_token)
  setAccessToken(result.access_token, result.token_type || 'Bearer', result.expires_in)
  return takeReturnHash()
}
