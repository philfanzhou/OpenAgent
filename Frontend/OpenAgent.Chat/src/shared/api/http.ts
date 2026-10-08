import { randomUuid } from '../browserCrypto'
import type { ConnectionMode } from '../contracts/types'

const legacyBaseUrlStorageKey = 'openagent.engine.base-url'
const routerStorageKey = 'openagent.router.base-url'
const engineStorageKey = 'openagent.direct-engine.base-url'
const connectionModeStorageKey = 'openagent.connection.mode'
const tokenStorageKey = 'openagent.auth.access-token'
const tokenTypeStorageKey = 'openagent.auth.token-type'
const tokenExpiryStorageKey = 'openagent.auth.expires-at'
const tokenEndpointStorageKey = 'openagent.auth.endpoint'
const refreshTokenStorageKey = 'openagent.auth.refresh-token'
const tenantStorageKey = 'openagent.auth.tenant-id'
export const AUTH_FAILURE_EVENT = 'openagent:auth-failure'
function defaultServiceUrl(port: number): string {
  if (typeof window !== 'undefined'
    && window.location.hostname
    && window.location.hostname !== 'localhost'
    && window.location.hostname !== '127.0.0.1') {
    return `${window.location.protocol}//${window.location.hostname}:${port}`
  }
  return `http://localhost:${port}`
}

const defaultRouterBaseUrl = import.meta.env.VITE_OPENAGENT_ROUTER_BASE_URL || defaultServiceUrl(5001)
const defaultEngineBaseUrl = import.meta.env.VITE_OPENAGENT_ENGINE_BASE_URL || defaultServiceUrl(5208)
const defaultTenantId = import.meta.env.VITE_OPENAGENT_TENANT_ID || 'development'

export function normalizeBaseUrl(value: string): string {
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
    clearExpiredAccessToken()
    return ''
  }
  const tokenEndpoint = sessionStorage.getItem(tokenEndpointStorageKey)
  if (tokenEndpoint && tokenEndpoint !== normalizeBaseUrl(requireBaseUrl())) return ''
  return sessionStorage.getItem(tokenStorageKey) || ''
}

export function getTokenType(): string {
  return sessionStorage.getItem(tokenTypeStorageKey) || ''
}

export function getAccessTokenExpiresAt(): number {
  return Number(sessionStorage.getItem(tokenExpiryStorageKey) || 0)
}

export function getRefreshToken(): string {
  const tokenEndpoint = sessionStorage.getItem(tokenEndpointStorageKey)
  if (tokenEndpoint && tokenEndpoint !== normalizeBaseUrl(requireBaseUrl())) return ''
  return sessionStorage.getItem(refreshTokenStorageKey) || ''
}

export function setRefreshToken(value: string): void {
  if (value.trim()) sessionStorage.setItem(refreshTokenStorageKey, value.trim())
  else sessionStorage.removeItem(refreshTokenStorageKey)
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
  clearExpiredAccessToken()
  sessionStorage.removeItem(refreshTokenStorageKey)
}

function clearExpiredAccessToken(): void {
  sessionStorage.removeItem(tokenStorageKey)
  sessionStorage.removeItem(tokenTypeStorageKey)
  sessionStorage.removeItem(tokenExpiryStorageKey)
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
  result.set('X-Trace-Id', randomUuid())
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
    // 后端统一返回 ProblemDetails：detail（兜底 title）+ camelCase traceId。
    const body = JSON.parse(raw) as { detail?: string; title?: string; traceId?: string }
    const message = safeErrorMessage(body.detail || body.title || fallback, fallback)
    const traceId = body.traceId ? safeErrorMessage(body.traceId, '') : ''
    return new ApiError(`${message}${traceId ? ` (TraceId: ${traceId})` : ''}`, response.status)
  } catch {
    return new ApiError(fallback, response.status)
  }
}

/** 绝对 URL 的统一请求入口：注入认证头与 X-Trace-Id，非 2xx 一律抛 ApiError。 */
export async function requestUrl(url: string, init: RequestInit = {}): Promise<Response> {
  const response = await fetch(url, { ...init, headers: headers(init.headers) })
  if (!response.ok) throw await readError(response)
  return response
}

/** 相对当前服务（Router/Engine）路径的请求。 */
export function send(path: string, init: RequestInit = {}): Promise<Response> {
  return requestUrl(`${requireBaseUrl()}${path}`, init)
}

/** JSON 请求体样板（POST/PUT/PATCH）。 */
export function jsonInit(method: string, body: unknown): RequestInit {
  return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
}

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await send(path, init)
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

/** 单文件 multipart 上传（'file' 字段）。 */
export function uploadForm<T>(path: string, file: File): Promise<T> {
  const form = new FormData()
  form.set('file', file, file.name)
  return request<T>(path, { method: 'POST', body: form })
}

/** 拉取二进制并转为对象 URL（预览用）。 */
export async function fetchObjectUrl(path: string): Promise<string> {
  const response = await send(path)
  return URL.createObjectURL(await response.blob())
}
