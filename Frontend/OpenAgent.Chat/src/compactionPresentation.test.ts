import { describe, expect, it } from 'vitest'
import { buildCompactionDisplay, buildCompactionTokenDisplay, latestCompactedContextTokens } from './compactionPresentation'
import type { ContextSummary } from './types'
import type { ConversationMessage } from './types'

function summary(overrides: Partial<ContextSummary> = {}): ContextSummary {
  return {
    compressionId: 'compression-1',
    strategy: 'summarization',
    trigger: 'Automatic',
    status: 'Succeeded',
    summary: 'The user selected the production tenant.',
    lastCompressedAt: '2026-08-19T00:00:00Z',
    compressedMessageCount: 8,
    originalStartSequence: 1,
    originalEndSequence: 8,
    originalTokenCount: 240,
    tokenCount: 120,
    originalHistoryRestored: false,
    sourceEndSequence: 8,
    ...overrides,
  }
}

describe('buildCompactionDisplay', () => {
  it('shows strategy, trigger and generated summary', () => {
    expect(buildCompactionDisplay(summary())).toEqual({
      strategy: '摘要',
      trigger: '自动',
      status: '已完成',
      tagType: 'success',
      detail: 'The user selected the production tenant.',
      recovered: false,
    })
  })

  it('shows failed recovery without hiding the result', () => {
    const display = buildCompactionDisplay(summary({
      trigger: 'Manual',
      status: 'Failed',
      summary: null,
      result: 'Original history restored for model invocation.',
      error: 'Summary model unavailable.',
      originalHistoryRestored: true,
    }))

    expect(display).toMatchObject({
      strategy: '摘要',
      trigger: '手动',
      status: '失败',
      tagType: 'danger',
      recovered: true,
      detail: 'Summary model unavailable.\n\nOriginal history restored for model invocation.',
    })
  })

  it('shows a skipped attempt without calling it unnecessary', () => {
    const display = buildCompactionDisplay(summary({
      trigger: 'Manual',
      status: 'Skipped',
      summary: null,
      result: 'Context is already within the compaction target budget.',
    }))

    expect(display).toMatchObject({
      trigger: '手动',
      status: '未执行',
      tagType: 'warning',
      detail: 'Context is already within the compaction target budget.',
    })
  })
})

describe('buildCompactionTokenDisplay', () => {
  it('uses the manual projection until another original message is appended', () => {
    const messages = [{ sequence: 8, role: 'assistant' }] as ConversationMessage[]
    expect(latestCompactedContextTokens(messages, [summary()])).toBe(120)
    expect(latestCompactedContextTokens([...messages, { sequence: 9 } as ConversationMessage], [summary()])).toBeUndefined()
    expect(latestCompactedContextTokens(messages, [summary({ status: 'Failed' })])).toBeUndefined()
  })
  it('shows exact before and after counts with retained ratio', () => {
    expect(buildCompactionTokenDisplay(summary())).toEqual({ before: 240, after: 120, retainedPercent: '50%' })
  })

  it('does not present legacy records without before-count data as zero', () => {
    expect(buildCompactionTokenDisplay(summary({ originalTokenCount: 0 }))).toEqual({ before: null, after: 120, retainedPercent: '—' })
  })
})
