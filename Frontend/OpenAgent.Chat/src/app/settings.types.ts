import type { Ref } from 'vue'
import type { AgentSummary, AuthConfig, ConnectionMode, CurrentUserContext } from '../shared/contracts/types'
import type { useSettings } from './useSettings'

export type SettingsDialogContext = ReturnType<typeof useSettings> & {
  connectionMode: Ref<ConnectionMode>
  routerUrl: Ref<string>
  engineUrl: Ref<string>
  tenantId: Ref<string>
  statusText: Ref<string>
  authConfig: Ref<AuthConfig | null>
  currentUser: Ref<CurrentUserContext | null>
  agents: Ref<AgentSummary[]>
  selectedAgentId: Ref<string>
  activeEndpointLabel: Ref<string>
  activeEndpointHost: Ref<string>
  connect: () => Promise<void>
  logout: () => Promise<void>
  testHealth: (path: '/health' | '/ready') => Promise<void>
}
