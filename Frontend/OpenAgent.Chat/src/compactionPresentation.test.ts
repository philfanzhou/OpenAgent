import { describe, expect, it } from 'vitest'
import { buildCompactionDisplay } from './compactionPresentation'
import type { ContextSummary } from './types'

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
      detail: 'The user selected the production tenant.',
      recovered: false,
    })
  })

  it('shows failed recovery without hiding the result', () => {
    const display = buildCompactionDisplay(summary({
      strategy: 'sliding_window',
      trigger: 'Manual',
      status: 'Failed',
      summary: null,
      result: 'Original history restored for model invocation.',
      originalHistoryRestored: true,
    }))

    expect(display).toMatchObject({
      strategy: '滑动窗口',
      trigger: '手动',
      status: '失败',
      recovered: true,
    })
  })
})
