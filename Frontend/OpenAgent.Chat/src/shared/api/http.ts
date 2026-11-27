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
export function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/$/, '')
}

export function normalizeServiceUrl(value: string): string {
  const trimmed = value.trim()
  if (!/^https?:\/\//i.test(trimmed)) throw new Error('Invalid service URL')
  const url = new URL(trimmed)
  if (url.username || url.password || url.search || url.hash) throw new Error('Invalid service URL')
  return url.href.replace(/\/+$/, '')
}

export function getConnectionMode(): ConnectionMode {
  const mode = localStorage.getItem(connectionModeStorageKey)
  if (mode) return mode === 'engine' ? 'engine' : 'router'
  if (localStorage.getItem(routerStorageKey) || localStorage.getItem(legacyBaseUrlStorageKey)) return 'router'
  if (localStorage.getItem(engineStorageKey)) return 'engine'
  return 'router'
}

export function setConnectionMode(value: ConnectionMode): void {
  localStorage.setItem(connectionModeStorageKey, value)
}

export function getRouterBaseUrl(): string {
  return localStorage.getItem(routerStorageKey)
    || localStorage.getItem(legacyBaseUrlStorageKey)
    || ''
}

export function setRouterBaseUrl(value: string): void {
  localStorage.setItem(routerStorageKey, normalizeBaseUrl(value))
  localStorage.removeItem(legacyBaseUrlStorageKey)
}

export function getEngineBaseUrl(): string {
  return localStorage.getItem(engineStorageKey) || ''
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
  if (tokenEndpoint && tokenEndpoint !== normalizeBaseUrl(activeBaseUrl())) return ''
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
  if (tokenEndpoint && tokenEndpoint !== normalizeBaseUrl(activeBaseUrl())) return ''
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
  return localStorage.getItem(tenantStorageKey) || 'development'
}

export function setTenantId(value: string): void {
  localStorage.setItem(tenantStorageKey, value.trim())
}

function activeBaseUrl(): string {
  return getConnectionMode() === 'router' ? getRouterBaseUrl() : getEngineBaseUrl()
}

function requireBaseUrl(): string {
  const mode = getConnectionMode()
  const value = activeBaseUrl()
  if (!value) throw new Error(`请先在设置中输入 ${mode === 'router' ? 'Router' : 'Engine'} 地址`)
  return value
}

function headers(extra: HeadersInit = {}): Headers {
  const result = new Headers({ Accept: 'application/json' })
  // Headers 实例与 [key, value] 数组形态没有可枚举属性，不能用对象展开合并。
  if (extra instanceof Headers) {
    extra.forEach((value, key) => result.set(key, value))
  } else if (Array.isArray(extra)) {
    for (const [key, value] of extra) result.set(key, value)
  } else {
    for (const [key, value] of Object.entries(extra)) result.set(key, value)
  }
  const token = getAccessToken()
  const tokenType = getTokenType() || 'Basic'
  if (token) result.set('Authorization', `${tokenType} ${token}`)
  result.set('X-Trace-Id', randomUuid())
  return result
}

/** 非流式请求的默认超时：网关/连接黑洞时避免 UI 无限期 pending。上传走独立的更长超时。 */
const defaultTimeoutMs = 120_000

/** 绝对 URL 的统一请求入口：注入认证头与 X-Trace-Id，非 2xx 一律抛 ApiError。 */
export async function requestUrl(url: string, init: RequestInit = {}, timeoutMs = defaultTimeoutMs): Promise<Response> {
  // 调用方自带 signal（如流式/取消）时不叠加超时；fetch 失败时原始异常照常抛出。
  const signal = init.signal
  const controller = signal ? undefined : new AbortController()
  const timer = controller ? setTimeout(() => controller.abort(), timeoutMs) : undefined
  try {
    const response = await fetch(url, { ...init, headers: headers(init.headers), signal: signal ?? controller?.signal })
    if (!response.ok) throw await readError(response)
    return response
  } catch (error) {
    if (controller && error instanceof DOMException && error.name === 'AbortError') {
      throw new ApiError('请求超时，请检查网络连接后重试。', 0)
    }
    throw error
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    /** 后端错误码名（如 RuntimeQueueFull）；只有 ProblemDetails 带 errorCode 时才有值。 */
    public readonly code?: string,
    /** 服务端建议的重试间隔（秒）。 */
    public readonly retryAfterSeconds?: number,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/** 把正整数形式的秒数解析出来；其它取值（缺失、0、负数、非数字）视为没有。 */
function retrySeconds(...values: (unknown)[]): number | undefined {
  for (const value of values) {
    const seconds = Number(value)
    if (Number.isFinite(seconds) && seconds > 0) return Math.ceil(seconds)
  }
  return undefined
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
    const body = JSON.parse(raw) as {
      detail?: string; title?: string; traceId?: string; errorCode?: unknown; retryAfterSeconds?: unknown
    }
    const message = safeErrorMessage(body.detail || body.title || fallback, fallback)
    const traceId = body.traceId ? safeErrorMessage(body.traceId, '') : ''
    // 平台错误映射出的 ProblemDetails 里 title 就是错误码名（如 RuntimeQueueFull），
    // 调用方据此给出针对性提示；没有 errorCode 的 ProblemDetails 不猜。
    const code = typeof body.errorCode === 'number' && typeof body.title === 'string' && !/\s/.test(body.title)
      ? body.title
      : undefined
    return new ApiError(
      `${message}${traceId ? ` (TraceId: ${traceId})` : ''}`,
      response.status,
      code,
      retrySeconds(body.retryAfterSeconds, response.headers.get('Retry-After')),
    )
  } catch {
    return new ApiError(fallback, response.status)
  }
}

/** 相对当前服务（Router/Engine）路径的请求。 */
export function send(path: string, init: RequestInit = {}, timeoutMs = defaultTimeoutMs): Promise<Response> {
  return requestUrl(`${requireBaseUrl()}${path}`, init, timeoutMs)
}

/** JSON 请求体样板（POST/PUT/PATCH）。 */
export function jsonInit(method: string, body: unknown): RequestInit {
  return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
}

export async function request<T>(path: string, init: RequestInit = {}, timeoutMs = defaultTimeoutMs): Promise<T> {
  const response = await send(path, init, timeoutMs)
  if (response.status === 204) return undefined as T
  return await response.json() as T
}

/** 单文件 multipart 上传（'file' 字段）。 */
export function uploadForm<T>(path: string, file: File): Promise<T> {
  const form = new FormData()
  form.set('file', file, file.name)
  // 上传大附件耗时波动大，给 10 分钟超时。
  return request<T>(path, { method: 'POST', body: form }, 600_000)
}

/** 拉取二进制并转为对象 URL（预览用）。 */
export async function fetchObjectUrl(path: string): Promise<string> {
  const response = await send(path, {}, 300_000)
  return URL.createObjectURL(await response.blob())
}
