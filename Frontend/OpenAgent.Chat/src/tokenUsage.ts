import type { ConversationMessage, TokenUsage } from './types'

export interface ConversationUsageSummary {
  available: boolean
  responseCount: number
  unavailableCount: number
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

export function summarizeConversationUsage(messages: ConversationMessage[]): ConversationUsageSummary {
  const responses = messages.filter(isAssistantResponse)
  const uniqueResponses = Array.from(new Map(responses.map(message => [message.messageId, message])).values())
  const unavailableCount = uniqueResponses.filter(message => !message.tokenUsage).length
  if (!uniqueResponses.length || unavailableCount > 0) {
    return {
      available: false,
      responseCount: uniqueResponses.length,
      unavailableCount,
    }
  }

  const usage = uniqueResponses.reduce<TokenUsage>((total, message) => {
    const current = message.tokenUsage!
    return {
      promptTokens: total.promptTokens + current.promptTokens,
      completionTokens: total.completionTokens + current.completionTokens,
      totalTokens: total.totalTokens + current.totalTokens,
    }
  }, { promptTokens: 0, completionTokens: 0, totalTokens: 0 })
  usage.cachedInputTokens = sumOptional(uniqueResponses.map(message => message.tokenUsage!.cachedInputTokens))
  usage.reasoningTokens = sumOptional(uniqueResponses.map(message => message.tokenUsage!.reasoningTokens))

  return {
    available: true,
    responseCount: uniqueResponses.length,
    unavailableCount: 0,
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
