import { jsonInit, request } from '../../shared/api/http'
import type { AgentConfigEntity, AgentSummary } from '../../shared/contracts/types'

export const api = {
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
