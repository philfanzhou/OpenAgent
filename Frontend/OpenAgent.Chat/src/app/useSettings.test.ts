import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { api as agentsApi } from '../features/agents/api'
import { api as mcpApi } from '../features/mcp/api'
import { api as modelsApi } from '../features/models/api'
import { getTenantId } from '../shared/api/http'
import type { AgentConfigEntity, AgentSummary, ConnectionMode } from '../shared/contracts/types'
import { useSettings } from './useSettings'

vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn() }, ElMessageBox: { confirm: vi.fn() } }))
vi.mock('../shared/api/http', async importOriginal => ({ ...await importOriginal<typeof import('../shared/api/http')>(), getTenantId: vi.fn(() => 'tenant-1') }))

function settings() {
  const options = {
    agents: ref<AgentSummary[]>([]), selectedAgentId: ref('agent-1'), selectedLlmProfileId: ref(''),
    connectionMode: ref<ConnectionMode>('engine'), routerUrl: ref(''), engineUrl: ref('http://engine'),
    notifyError: vi.fn(),
  }
  return { state: useSettings(options), options }
}

beforeEach(() => vi.restoreAllMocks())

describe('settings composition', () => {
  it('saves bindings selected in independent modules through the Agent configuration', async () => {
    const { state, options } = settings()
    const config: AgentConfigEntity = {
      agentId: 'agent-1', name: 'Agent', description: '', status: 0, currentVersion: '',
      config: {
        instructions: '', maxTurns: 5, mcp: { servers: [] },
        skills: { enabledSkills: [], instances: [] }, rag: { enabled: false, enabledRagInstanceIds: [], instances: [] }
      },
    }
    state.config.value = config
    state.toggleMcpBinding({ name: 'orders', url: 'http://mcp', type: 'Http' }, true)
    state.toggleSkillBinding({ skillId: 'reports', name: 'Reports', enabled: false }, true)
    const save = vi.spyOn(agentsApi, 'saveAgentConfig').mockResolvedValue(config)

    await state.saveConfig()

    expect(save).toHaveBeenCalledWith('agent-1', expect.objectContaining({
      config: expect.objectContaining({
        mcp: { enabledServerIds: ['orders'], servers: [] },
        skills: { enabledSkills: ['reports'], instances: [] },
      }),
    }))
    expect(options.agents.value[0]?.tenantId).toBe(getTenantId())
    expect(options.notifyError).not.toHaveBeenCalled()
  })

  it('clears capability bindings when the selected Agent changes', () => {
    const { state } = settings()
    state.toggleMcpBinding({ name: 'private-orders', url: 'http://mcp', type: 'Http' }, true)
    state.toggleSkillBinding({ skillId: 'private-report', name: 'Report', enabled: false }, true)
    state.ragInstances.value = [{ id: 'private-knowledge', name: 'Knowledge', enabled: true, type: 'ragflow', collectionName: 'default', apiEndpoint: 'http://rag' }]

    state.handleAgentChange()

    expect(state.boundMcpServers.value).toEqual([])
    expect(state.boundSkills.value).toEqual([])
    expect(state.ragInstances.value).toEqual([])
    expect(state.config.value).toBeNull()
  })

  it('keeps module failures in the shared error presentation flow', async () => {
    const { state, options } = settings()
    const error = new Error('MCP catalog unavailable')
    vi.spyOn(mcpApi, 'listMcpProfiles').mockRejectedValue(error)
    vi.spyOn(modelsApi, 'listLlmProfiles').mockResolvedValue([])

    await state.loadMcpProfiles()

    expect(options.notifyError).toHaveBeenCalledWith(error)
  })
})
