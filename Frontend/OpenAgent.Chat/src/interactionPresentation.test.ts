import { describe, expect, it } from 'vitest'
import {
  collectAllInteractions,
  formatInteractionTime,
  interactionSourceLabel,
  interactionStatusLabel,
  interactionStatusTagType,
  prettyInteractionPayload,
  shortTraceId,
} from './interactionPresentation'
import type { LlmInteractionRecord } from './types'

function record(interactionId: string): LlmInteractionRecord {
  return {
    interactionId,
    tenantId: 'development',
    userId: 'anonymous',
    conversationId: 'conv-1',
    traceId: 'trace-1',
    agentId: 'default',
    source: 0,
    provider: 'kimi',
    apiFormat: 'OpenAIChatCompletions',
    modelId: 'kimi-k2.6',
    streamed: true,
    callIndex: 0,
    requestJson: '{}',
    responseJson: '{}',
    tokenUsage: { promptTokens: 1, completionTokens: 2, totalTokens: 3 },
    status: 0,
    errorMessage: null,
    startedAt: '2026-09-20T01:47:00.187764+00:00',
    durationMs: 91,
  }
}

describe('collectAllInteractions', () => {
  it('aggregates pages until a short page arrives', async () => {
    const pages = [
      Array.from({ length: 2 }, (_, index) => record(`a-${index}`)),
      Array.from({ length: 2 }, (_, index) => record(`b-${index}`)),
      [record('c-0')],
    ]
    const calls: Array<[number, number]> = []
    const records = await collectAllInteractions(async (skip, take) => {
      calls.push([skip, take])
      return pages[skip / take] ?? []
    }, 2)

    expect(records.map(item => item.interactionId)).toEqual(['a-0', 'a-1', 'b-0', 'b-1', 'c-0'])
    expect(calls).toEqual([[0, 2], [2, 2], [4, 2]])
  })

  it('stops at maxPages even if the endpoint keeps returning full pages', async () => {
    const records = await collectAllInteractions(async () => [record('x')], 1, 3)
    expect(records).toHaveLength(3)
  })
})

describe('interaction labels', () => {
  it('maps source and status enums to display text', () => {
    expect(interactionSourceLabel(0)).toBe('对话轮次')
    expect(interactionSourceLabel(1)).toBe('压缩摘要')
    expect(interactionStatusLabel(0)).toBe('成功')
    expect(interactionStatusLabel(1)).toBe('失败')
    expect(interactionStatusLabel(2)).toBe('已取消')
    expect(interactionStatusTagType(0)).toBe('success')
    expect(interactionStatusTagType(1)).toBe('danger')
    expect(interactionStatusTagType(2)).toBe('info')
  })
})

describe('trace and time formatting', () => {
  it('shortens long trace ids but keeps short ones intact', () => {
    expect(shortTraceId('pr85-verify-turn-002')).toBe('pr85-ver…')
    expect(shortTraceId('short')).toBe('short')
  })

  it('formats interaction timestamps as local time', () => {
    expect(formatInteractionTime('2026-09-20T01:47:00Z')).toMatch(/^\d{2}:\d{2}:\d{2}$/)
    expect(formatInteractionTime('not-a-date')).toBe('not-a-date')
  })
})

describe('prettyInteractionPayload', () => {
  it('pretty-prints valid JSON payloads', () => {
    expect(prettyInteractionPayload('{"a":1}')).toBe('{\n  "a": 1\n}')
  })

  it('returns truncated payloads as-is when they no longer parse', () => {
    expect(prettyInteractionPayload('{"a":"…')).toBe('{"a":"…')
    expect(prettyInteractionPayload(null)).toBe('')
    expect(prettyInteractionPayload(undefined)).toBe('')
  })
})
