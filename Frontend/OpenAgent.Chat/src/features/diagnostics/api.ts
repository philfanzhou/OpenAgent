import { normalizeBaseUrl, request, requestUrl, send } from '../../shared/api/http'
import type { HealthReport, LlmInteractionRecord } from '../../shared/contracts/types'

export async function fetchHealthReport(baseUrl: string): Promise<HealthReport> {
  const response = await requestUrl(`${normalizeBaseUrl(baseUrl)}/health/report`)
  return await response.json() as HealthReport
}

export const api = {
  async health(path: '/health' | '/ready'): Promise<void> {
    await send(path)
  },

  listLlmInteractions(conversationId: string, skip = 0, take = 200): Promise<LlmInteractionRecord[]> {
    return request<LlmInteractionRecord[]>(`/api/v1/agent/conversations/${encodeURIComponent(conversationId)}/llm-interactions?skip=${skip}&take=${take}`)
  }
}
