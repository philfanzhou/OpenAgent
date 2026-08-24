import { describe, expect, it } from 'vitest'
import { estimateTokens, formatCacheHitRate, formatTokenBreakdown, formatTokenUsage, summarizeConversationUsage } from './tokenUsage'
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

    // 未上报 usage 的回复改为内容长度预估，面板不再整体隐藏。
    expect(summary.available).toBe(true)
    expect(summary.estimated).toBe(true)
    expect(summary.unavailableCount).toBe(1)
    expect(summary.usage!.promptTokens).toBe(10)
    expect(summary.usage!.totalTokens).toBe(14 + estimateTokens('response'))
  })

  it('estimates tokens from content length with CJK awareness', () => {
    expect(estimateTokens('abcd')).toBe(1) // 4/4
    expect(estimateTokens('你好')).toBe(2) // 2 * 0.75 → ceil(1.5)
    expect(estimateTokens('')).toBe(0)
  })

  it('keeps an estimated total visible while a reply is still streaming', () => {
    const streaming = response('stream-1', 0, 0, 0)
    streaming.content = '正在生成的回复内容'

    const summary = summarizeConversationUsage([response('done-1', 10, 4, 14), streaming])

    expect(summary.available).toBe(true)
    expect(summary.estimated).toBe(true)
    expect(summary.usage!.completionTokens).toBe(4 + estimateTokens(streaming.content))
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

  it('computes cache hit rate from cached and prompt tokens', () => {
    expect(formatCacheHitRate(800, 1200)).toBe('66.7%')
    expect(formatCacheHitRate(0, 500)).toBe('0.0%')
  })

  it('omits cache hit rate when provider does not report caching or prompts', () => {
    expect(formatCacheHitRate(null, 500)).toBeUndefined()
    expect(formatCacheHitRate(undefined, 500)).toBeUndefined()
    expect(formatCacheHitRate(800, 0)).toBeUndefined()
    expect(formatCacheHitRate(800, null)).toBeUndefined()
  })

  it('clamps cache hit rate at 100 percent', () => {
    expect(formatCacheHitRate(600, 500)).toBe('100.0%')
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
