import type { ConversationMessage, TokenUsage } from './types'

export interface ConversationUsageSummary {
  available: boolean
  responseCount: number
  unavailableCount: number
  /** true 表示部分回复尚未上报 usage，合计值包含内容长度估算，展示时应带 ≈。 */
  estimated: boolean
  usage?: TokenUsage
}

export function formatTokenUsage(usage?: TokenUsage | null): string {
  if (!usage) return '暂不可用'
  return `输入 ${formatTokenCount(usage.promptTokens)} · 输出 ${formatTokenCount(usage.completionTokens)} · 总计 ${formatTokenCount(usage.totalTokens)}`
}

export function formatTokenBreakdown(usage?: TokenUsage | null): string | undefined {
  if (!usage) return undefined
  const details: string[] = []
  if (usage.cachedInputTokens != null) details.push(`缓存输入 ${formatTokenCount(usage.cachedInputTokens)}`)
  if (usage.reasoningTokens != null) details.push(`思考 ${formatTokenCount(usage.reasoningTokens)}`)
  return details.length ? details.join(' · ') : undefined
}

const CJK_CHARACTER = /[぀-ヿ㐀-䶿一-鿿가-힯豈-﫿]/

/**
 * 粗略 token 估算：CJK ≈0.75 token/字符（约 1.3 字符/token），
 * 其他脚本 ≈0.25 token/字符（约 4 字符/token）。仅用于 Provider 未上报时的兜底展示。
 */
export function estimateTokens(text: string): number {
  let cjk = 0
  let total = 0
  for (const character of text) {
    total += 1
    if (CJK_CHARACTER.test(character)) cjk += 1
  }
  return Math.ceil(cjk * 0.75 + (total - cjk) / 4)
}

export function summarizeConversationUsage(messages: ConversationMessage[]): ConversationUsageSummary {
  const responses = messages.filter(isAssistantResponse)
  const uniqueResponses = Array.from(new Map(responses.map(message => [message.messageId, message])).values())
  const withUsage = uniqueResponses.filter(message => message.tokenUsage)
  if (!uniqueResponses.length) {
    return { available: false, responseCount: 0, unavailableCount: 0, estimated: false }
  }

  // 已完成响应取真实值；缺失 usage 的（流式中或未上报）按内容长度估算，
  // 保证会话进行中面板始终有数值可看，完成后自动收敛为精确值。
  const usage = withUsage.reduce<TokenUsage>((total, message) => {
    const current = message.tokenUsage!
    return {
      promptTokens: total.promptTokens + current.promptTokens,
      completionTokens: total.completionTokens + current.completionTokens,
      totalTokens: total.totalTokens + current.totalTokens,
    }
  }, { promptTokens: 0, completionTokens: 0, totalTokens: 0 })

  let estimated = false
  for (const message of uniqueResponses) {
    if (message.tokenUsage) continue
    const outputEstimate = estimateTokens(message.content)
    usage.completionTokens += outputEstimate
    usage.totalTokens += outputEstimate
    estimated = true
  }

  usage.cachedInputTokens = sumOptional(withUsage.map(message => message.tokenUsage!.cachedInputTokens))
  usage.reasoningTokens = sumOptional(withUsage.map(message => message.tokenUsage!.reasoningTokens))

  return {
    available: true,
    responseCount: uniqueResponses.length,
    unavailableCount: uniqueResponses.length - withUsage.length,
    estimated,
    usage,
  }
}

function isAssistantResponse(message: ConversationMessage): boolean {
  return message.role === 'assistant' && !message.toolName
}

function sumOptional(counts: Array<number | null | undefined>): number | undefined {
  return counts.every(count => count != null)
    ? counts.reduce<number>((total, count) => total + count!, 0)
    : undefined
}

export function formatTokenCount(count: number): string {
  return count.toLocaleString('zh-CN')
}

export function formatCacheHitRate(
  cachedInputTokens?: number | null,
  promptTokens?: number | null,
): string | undefined {
  if (cachedInputTokens == null || !promptTokens) return undefined
  const rate = Math.min(100, (cachedInputTokens / promptTokens) * 100)
  return `${rate.toFixed(1)}%`
}
