export * from './shared/api/http'
export { fetchHealth, fetchHealthReport } from './features/diagnostics/api'
export { makeLocalConversation } from './features/conversations/api'
import { api as authApi } from './features/auth/api'
import { api as diagnosticsApi } from './features/diagnostics/api'
import { api as agentsApi } from './features/agents/api'
import { api as conversationsApi } from './features/conversations/api'
import { api as filesApi } from './features/files/api'
import { api as modelsApi } from './features/models/api'
import { api as skillsApi } from './features/skills/api'
import { api as mcpApi } from './features/mcp/api'
import { api as ragApi } from './features/rag/api'
import { api as chatApi } from './features/chat/api'

// Compatibility facade for existing consumers; feature implementations use their own API.
export const api = {
  ...authApi,
  ...diagnosticsApi,
  ...agentsApi,
  ...conversationsApi,
  ...filesApi,
  ...modelsApi,
  ...skillsApi,
  ...mcpApi,
  ...ragApi,
  ...chatApi,
}
