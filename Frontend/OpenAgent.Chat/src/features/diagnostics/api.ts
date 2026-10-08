import { normalizeBaseUrl, request, requestUrl, send } from '../../shared/api/http'
import type { HealthReport, LlmInteractionRecord, NativeHealthReport } from '../../shared/contracts/types'

export async function fetchHealthReport(baseUrl: string): Promise<HealthReport> {
  const response = await requestUrl(`${normalizeBaseUrl(baseUrl)}/health/report`)
  return await response.json() as HealthReport
}

export async function fetchHealth(baseUrl: string, path: '/health' | '/ready'): Promise<NativeHealthReport> {
  const response = await requestUrl(`${normalizeBaseUrl(baseUrl)}${path}`)
  // Router 的健康端点以纯文本返回 "Healthy"/"Degraded"/"Unhealthy"，
  // Engine 返回 JSON HealthReport；统一做兜底解析。
  const text = await response.text()
  try {
    return JSON.parse(text) as NativeHealthReport
  } catch {
    return { status: text.trim(), entries: {} } as NativeHealthReport
  }
}

export const api = {
  async health(path: '/health' | '/ready'): Promise<void> {
    await send(path)
  },

  listLlmInteractions(conversationId: string, skip = 0, take = 200): Promise<LlmInteractionRecord[]> {
    return request<LlmInteractionRecord[]>(`/api/v1/agent/conversations/${encodeURIComponent(conversationId)}/llm-interactions?skip=${skip}&take=${take}`)
  }
}
