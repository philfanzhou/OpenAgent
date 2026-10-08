import { jsonInit, request } from '../../shared/api/http'
import type { RagConfig, RagInstanceConfig, RagTestResult } from '../../shared/contracts/types'

export const api = {
  getRagConfig(agentId: string): Promise<RagConfig> {
    return request<RagConfig>(`/api/v1/admin/rag?agentId=${encodeURIComponent(agentId)}`)
  },

  saveRag(id: string, agentId: string, instance: RagInstanceConfig): Promise<RagInstanceConfig> {
    return request<RagInstanceConfig>(`/api/v1/admin/rag/${encodeURIComponent(id)}?agentId=${encodeURIComponent(agentId)}`, jsonInit('PUT', instance))
  },

  deleteRag(id: string, agentId: string): Promise<void> {
    return request<void>(`/api/v1/admin/rag/${encodeURIComponent(id)}?agentId=${encodeURIComponent(agentId)}`, { method: 'DELETE' })
  },

  testRag(instance: RagInstanceConfig): Promise<RagTestResult> {
    return request<RagTestResult>('/api/v1/admin/rag/test-connection', jsonInit('POST', instance))
  }
}
