import { jsonInit, request } from '../../shared/api/http'
import type { AuthConfig, AuthTokenResponse, CurrentUserContext } from '../../shared/contracts/types'

export const api = {
  getAuthConfig(): Promise<AuthConfig> {
    return request<AuthConfig>('/api/v1/auth/config')
  },

  passwordLogin(username: string, password: string): Promise<AuthTokenResponse> {
    return request<AuthTokenResponse>('/api/v1/auth/password/token', jsonInit('POST', { username, password }))
  },

  getCurrentUser(): Promise<CurrentUserContext> {
    return request<CurrentUserContext>('/api/v1/agent/me')
  }
}
