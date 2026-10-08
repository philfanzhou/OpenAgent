import { jsonInit, request } from '../../shared/api/http'
import type { McpServerConfig, McpTestResult } from '../../shared/contracts/types'

export const api = {
  testMcp(server: McpServerConfig, agentId?: string): Promise<McpTestResult> {
    return request<McpTestResult>('/api/v1/admin/mcp/test-connection', jsonInit('POST', { agentId, server, action: 'discover' }))
  },

  listMcpProfiles(): Promise<McpServerConfig[]> {
    return request<McpServerConfig[]>('/api/v1/admin/mcp')
  },

  getMcpProfile(id: string): Promise<McpServerConfig> {
    return request<McpServerConfig>(`/api/v1/admin/mcp/${encodeURIComponent(id)}`)
  },

  saveMcpProfile(id: string, server: McpServerConfig): Promise<McpServerConfig> {
    return request<McpServerConfig>(`/api/v1/admin/mcp/${encodeURIComponent(id)}`, jsonInit('PUT', server))
  },

  deleteMcpProfile(id: string): Promise<void> {
    return request<void>(`/api/v1/admin/mcp/${encodeURIComponent(id)}`, { method: 'DELETE' })
  }
}
