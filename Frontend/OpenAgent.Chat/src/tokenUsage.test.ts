import { describe, expect, it } from 'vitest'
import { formatTokenBreakdown, formatTokenUsage, summarizeConversationUsage } from './tokenUsage'
import type { ConversationMessage } from './types'

describe('token usage presentation', () => {
  it('formats provider-reported response usage and keeps breakdowns separate', () => {
    const usage = {
      promptTokens: 1200,
      completionTokens: 34,
      totalTokens: 1234,
      cachedInputTokens: 800,
      reasoningTokens: 12,
    }

    expect(formatTokenUsage(usage)).toBe('输入 1,200 · 输出 34 · 总计 1,234')
    expect(formatTokenBreakdown(usage)).toBe('缓存输入 800 · 思考 12')
  })

  it('shows unavailable instead of estimating missing provider usage', () => {
    expect(formatTokenUsage(undefined)).toBe('暂不可用')
  })

  it('sums each persisted assistant response once', () => {
    const repeated = response('response-1', 10, 4, 14)
    const summary = summarizeConversationUsage([
      repeated,
      repeated,
      response('response-2', 20, 6, 26),
    ])

    expect(summary.available).toBe(true)
    expect(summary.responseCount).toBe(2)
    expect(summary.usage).toMatchObject({ promptTokens: 30, completionTokens: 10, totalTokens: 40 })
  })

  it('marks the conversation total unavailable when any response lacks usage', () => {
    const missing = response('response-2', 0, 0, 0)
    delete missing.tokenUsage

    const summary = summarizeConversationUsage([response('response-1', 10, 4, 14), missing])

    expect(summary).toEqual({ available: false, responseCount: 2, unavailableCount: 1 })
  })

  it('does not count stored tool-call messages as responses', () => {
    const toolCall: ConversationMessage = {
      messageId: 'tool-call-1', sequence: 1, role: 'assistant', content: 'Calling search',
      toolName: 'search', timestamp: new Date(0).toISOString(),
    }

    const summary = summarizeConversationUsage([toolCall, response('response-1', 10, 4, 14)])

    expect(summary.responseCount).toBe(1)
    expect(summary.available).toBe(true)
  })
})

function response(id: string, promptTokens: number, completionTokens: number, totalTokens: number): ConversationMessage {
  return {
    messageId: id,
    sequence: 1,
    role: 'assistant',
    content: 'response',
    timestamp: new Date(0).toISOString(),
    tokenUsage: { promptTokens, completionTokens, totalTokens },
  }
}
