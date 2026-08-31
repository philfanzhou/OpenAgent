import { computed, ref, type Ref } from 'vue'
import { ElMessage } from 'element-plus'
import {
  api,
  ApiError,
  AUTH_FAILURE_EVENT,
  clearAuthentication,
  getAccessToken,
  getConnectionMode,
  getEngineBaseUrl,
  getRouterBaseUrl,
  getTenantId,
  setAccessToken,
  setConnectionMode,
  setEngineBaseUrl,
  setRouterBaseUrl,
  setTenantId,
} from '../api'
import {
  beginOidcLogin,
  buildOidcLogoutUrl,
  cleanAuthorizationCallbackUrl,
  clearAuthState,
  completeOidcLogin,
  LOGIN_HASH,
  markReauthenticationRequired,
  sanitizeReturnHash,
  WORKSPACE_HASH,
} from '../auth'
import type { AuthConfig, ConnectionMode, CurrentUserContext } from '../types'

interface AuthenticationOptions {
  currentUser: Ref<CurrentUserContext | null>
  loadWorkspace: () => Promise<void>
  resetWorkspace: () => void
  cancelStreams: (reason: 'logout') => Promise<void>
  closeSettings: () => void
  notifyError: (error: unknown) => void
}

export function useAuthentication(options: AuthenticationOptions) {
  const connectionMode = ref<ConnectionMode>(getConnectionMode())
  const routerUrl = ref(getRouterBaseUrl())
  const engineUrl = ref(getEngineBaseUrl())
  const token = ref(getAccessToken())
  const tenantId = ref(getTenantId())
  const statusText = ref('未连接')
  const authConfig = ref<AuthConfig | null>(null)
  const authLoading = ref(false)
  const authView = ref<'restoring' | 'login' | 'workspace' | 'forbidden'>('restoring')
  const authError = ref('')
  const authReason = ref('')
  const authReturnHash = ref(sanitizeReturnHash(window.location.hash))

  const activeEndpointUrl = computed(() => connectionMode.value === 'router' ? routerUrl.value : engineUrl.value)
  const activeEndpointLabel = computed(() => connectionMode.value === 'router' ? 'Router' : 'Engine')
  const activeEndpointHost = computed(() => {
    try { return new URL(activeEndpointUrl.value).host }
    catch { return activeEndpointUrl.value || '未配置' }
  })

  async function loadAuthConfig(): Promise<void> {
    authConfig.value = await api.getAuthConfig()
  }

  function resetSessionWorkspace(): void {
    void options.cancelStreams('logout')
    options.resetWorkspace()
    token.value = ''
    options.closeSettings()
    statusText.value = '未连接'
  }

  function showLogin(reason = ''): void {
    if (window.location.hash && window.location.hash !== LOGIN_HASH && window.location.hash !== '#/forbidden') {
      authReturnHash.value = sanitizeReturnHash(window.location.hash)
    }
    resetSessionWorkspace()
    authView.value = 'login'
    authReason.value = reason
    window.history.replaceState(null, document.title, `${window.location.pathname}${LOGIN_HASH}`)
  }

  async function establishSession(returnHash = WORKSPACE_HASH): Promise<void> {
    const user = await api.getCurrentUser()
    if (!user.isAuthenticated) throw new Error('服务端未建立已认证身份')
    options.currentUser.value = user
    if (user.tenantId) {
      tenantId.value = user.tenantId
      setTenantId(user.tenantId)
    }
    token.value = getAccessToken()
    authError.value = ''
    authReason.value = ''
    authView.value = 'workspace'
    statusText.value = '已连接'
    window.history.replaceState(null, document.title, `${window.location.pathname}${sanitizeReturnHash(returnHash)}`)
    await options.loadWorkspace()
  }

  async function connect(): Promise<void> {
    setConnectionMode(connectionMode.value)
    setRouterBaseUrl(routerUrl.value)
    setEngineBaseUrl(engineUrl.value)
    setTenantId(tenantId.value)
    if (token.value && !getAccessToken()) {
      clearAuthState()
      showLogin('服务地址已变更，请针对新的认证边界重新登录。')
      await detectAuthentication(false)
      return
    }
    try {
      await api.health('/ready')
      await loadAuthConfig()
      statusText.value = '已连接'
      options.closeSettings()
      await options.loadWorkspace()
    } catch (error) {
      statusText.value = '连接失败'
      options.notifyError(error)
    }
  }

  function updateLoginConnection(value: {
    mode: ConnectionMode
    routerUrl: string
    engineUrl: string
    tenantId: string
  }): void {
    connectionMode.value = value.mode
    routerUrl.value = value.routerUrl
    engineUrl.value = value.engineUrl
    tenantId.value = value.tenantId
    setConnectionMode(value.mode)
    setRouterBaseUrl(value.routerUrl)
    setEngineBaseUrl(value.engineUrl)
    setTenantId(value.tenantId)
    authConfig.value = null
    authError.value = ''
    void detectAuthentication()
  }

  async function loginWithPassword(credentials: { username: string; password: string }): Promise<void> {
    authLoading.value = true
    authError.value = ''
    try {
      if (!authConfig.value?.development || authConfig.value.mode !== 'Basic' || !authConfig.value.password.enabled) {
        throw new Error('Development Basic 登录未启用')
      }
      const result = await api.passwordLogin(credentials.username, credentials.password)
      setAccessToken(result.access_token, result.token_type || 'Basic', result.expires_in)
      token.value = result.access_token
      await establishSession(authReturnHash.value)
    } catch {
      clearAuthentication()
      token.value = ''
      authError.value = '登录失败，请检查连接、账号和租户后重试。'
    } finally {
      authLoading.value = false
    }
  }

  async function loginWithOidc(): Promise<void> {
    if (!authConfig.value) return
    authLoading.value = true
    authError.value = ''
    try {
      await beginOidcLogin(authConfig.value, authReturnHash.value)
    } catch (error) {
      authError.value = error instanceof Error
        ? error.message
        : '无法启动企业登录，请检查身份提供方配置。'
      authLoading.value = false
    }
  }

  function returnToWorkspace(): void {
    authView.value = 'workspace'
    window.history.replaceState(null, document.title, `${window.location.pathname}${WORKSPACE_HASH}`)
  }

  function handleAuthenticationFailure(event: Event): void {
    const status = (event as CustomEvent<{ status: number }>).detail?.status
    if (status === 401) {
      clearAuthState()
      showLogin('登录已过期，请重新登录。')
    } else if (status === 403 && authView.value === 'workspace') {
      authView.value = 'forbidden'
      window.history.replaceState(null, document.title, `${window.location.pathname}#/forbidden`)
    }
  }

  async function detectAuthentication(restoreSession = true): Promise<void> {
    authLoading.value = true
    authError.value = ''
    try {
      await loadAuthConfig()
      if (restoreSession && getAccessToken()) {
        authView.value = 'restoring'
        await establishSession(authReturnHash.value)
        return
      }
      showLogin(authReason.value)
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) return
      authConfig.value = null
      authView.value = 'login'
      authError.value = '无法读取服务认证配置，请检查连接地址后重试。'
    } finally {
      authLoading.value = false
    }
  }

  async function logout(): Promise<void> {
    await options.cancelStreams('logout')
    let oidcLogoutUrl: string | null = null
    if (authConfig.value?.mode === 'JwtBearer') {
      try {
        oidcLogoutUrl = await buildOidcLogoutUrl(authConfig.value)
      } catch {
        // Local cleanup and prompt=login still protect the next login when
        // the identity provider's logout metadata is unavailable.
      }
    }
    clearAuthState()
    markReauthenticationRequired()
    authConfig.value = null
    authError.value = ''
    token.value = ''
    options.currentUser.value = null
    statusText.value = '未连接'
    showLogin('你已安全退出，当前会话中的敏感信息已清理。')
    if (oidcLogoutUrl) {
      window.location.assign(oidcLogoutUrl)
      return
    }
    void detectAuthentication(false)
  }

  async function testHealth(path: '/health' | '/ready'): Promise<void> {
    try {
      await api.health(path)
      ElMessage.success(path === '/health' ? 'Live 健康检查通过' : 'Ready 健康检查通过')
    } catch (error) {
      options.notifyError(error)
    }
  }

  async function initializeAuthentication(): Promise<void> {
    window.addEventListener(AUTH_FAILURE_EVENT, handleAuthenticationFailure)
    const parameters = new URLSearchParams(window.location.search)
    const hasCallback = parameters.has('code') || parameters.has('error')
    if (hasCallback) {
      const callbackParameters = cleanAuthorizationCallbackUrl()
      authView.value = 'restoring'
      authLoading.value = true
      try {
        await loadAuthConfig()
        if (!authConfig.value) throw new Error('Authentication configuration unavailable')
        const returnHash = await completeOidcLogin(authConfig.value, callbackParameters)
        await establishSession(returnHash)
      } catch {
        clearAuthState()
        authError.value = '企业登录未完成或回调已失效，请重新登录。'
        authView.value = 'login'
      } finally {
        authLoading.value = false
      }
      return
    }
    if (window.location.hash === LOGIN_HASH) clearAuthentication()
    await detectAuthentication(window.location.hash !== LOGIN_HASH)
  }

  function disposeAuthentication(): void {
    window.removeEventListener(AUTH_FAILURE_EVENT, handleAuthenticationFailure)
  }

  return {
    connectionMode,
    routerUrl,
    engineUrl,
    token,
    tenantId,
    statusText,
    authConfig,
    authLoading,
    authView,
    authError,
    authReason,
    activeEndpointUrl,
    activeEndpointLabel,
    activeEndpointHost,
    connect,
    updateLoginConnection,
    loginWithPassword,
    loginWithOidc,
    returnToWorkspace,
    detectAuthentication,
    logout,
    testHealth,
    initializeAuthentication,
    disposeAuthentication,
  }
}
