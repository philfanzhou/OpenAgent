import { jsonInit, request } from '../../shared/api/http'
import type { LlmProviderProfile, LlmTestResult } from '../../shared/contracts/types'

export const api = {
  listLlmProfiles(): Promise<LlmProviderProfile[]> {
    return request<LlmProviderProfile[]>('/api/v1/admin/llm')
  },

  saveLlmProfile(id: string, profile: LlmProviderProfile): Promise<LlmProviderProfile> {
    return request<LlmProviderProfile>(`/api/v1/admin/llm/${encodeURIComponent(id)}`, jsonInit('PUT', profile))
  },

  deleteLlmProfile(id: string): Promise<void> {
    return request<void>(`/api/v1/admin/llm/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },

  testLlmProfile(profile: LlmProviderProfile): Promise<LlmTestResult> {
    return request<LlmTestResult>('/api/v1/admin/llm/test-connection', jsonInit('POST', profile))
  }
}
