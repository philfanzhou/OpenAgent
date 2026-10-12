import { jsonInit, request } from '../../shared/api/http'
import type { AgentConfigEntity, AgentSummary, RuntimeDescriptor } from '../../shared/contracts/types'

export const api = {
  listRuntimes(): Promise<RuntimeDescriptor[]> {
    return request<RuntimeDescriptor[]>('/api/v1/agent/runtimes')
  },

  testRuntime(harness: string): Promise<{ success: boolean; stage: string; message: string }> {
    return request(`/api/v1/agent/runtimes/${encodeURIComponent(harness)}/test`, { method: 'POST' })
  },

  listAgents(): Promise<AgentSummary[]> {
    return request<AgentSummary[]>('/api/v1/agent/agents')
  },

  getAgentConfig(id: string): Promise<AgentConfigEntity> {
    return request<AgentConfigEntity>(`/api/v1/admin/agents/${encodeURIComponent(id)}`)
  },

  saveAgentConfig(id: string, config: AgentConfigEntity): Promise<AgentConfigEntity> {
    return request<AgentConfigEntity>(`/api/v1/admin/agents/${encodeURIComponent(id)}/config`, jsonInit('PUT', config))
  }
}
