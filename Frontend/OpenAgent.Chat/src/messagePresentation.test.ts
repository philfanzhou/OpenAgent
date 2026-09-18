import { describe, expect, it } from 'vitest'
import { buildConversationTimeline, buildDisplayMessages } from './messagePresentation'
import type { ContextSummary, ConversationMessage } from './types'

function message(partial: Partial<ConversationMessage> & { role: ConversationMessage['role'] }): ConversationMessage {
  return {
    messageId: 'm-' + Math.random().toString(36).slice(2),
    sequence: 1,
    content: '',
    timestamp: '2026-09-18T00:00:00.000Z',
    ...partial,
  }
}

describe('messagePresentation', () => {
  it('merges consecutive assistant rows into one display message', () => {
    const rows = [
      message({ sequence: 2, role: 'user', content: 'hi' }),
      message({ sequence: 3, role: 'assistant', content: '', toolCallId: 'call-1', toolName: 'read_file' }),
      message({ sequence: 4, role: 'tool', content: 'result', toolCallId: 'call-1', toolName: 'read_file' }),
      message({ sequence: 5, role: 'assistant', content: 'final answer' }),
    ]

    const display = buildDisplayMessages(rows)

    expect(display.map(item => item.role)).toEqual(['user', 'assistant'])
    const assistant = display[1]!
    expect(assistant.content).toBe('final answer')
    expect(assistant.toolActivities?.map(tool => tool.name)).toEqual(['read_file'])
  })

  it('keeps a single copy when a partial row is followed by the complete text', () => {
    // Interrupted runs can persist a partial assistant row and later the complete
    // one; concatenating both would show the overlapping fragment twice.
    const rows = [
      message({ sequence: 1, role: 'user', content: 'hi' }),
      message({ sequence: 2, role: 'assistant', content: 'The first half' }),
      message({ sequence: 3, role: 'assistant', content: 'The first half and the second half' }),
    ]

    const display = buildDisplayMessages(rows)

    expect(display[1]!.content).toBe('The first half and the second half')
  })

  it('places a context summary after the messages it compacted', () => {
    const messages = [
      message({ sequence: 1, role: 'user', content: 'first' }),
      message({ sequence: 2, role: 'assistant', content: 'answer 1' }),
      message({ sequence: 3, role: 'user', content: 'second' }),
      message({ sequence: 4, role: 'assistant', content: 'answer 2' }),
    ]
    const summary: ContextSummary = {
      compressionId: 'c-1',
      strategy: 'summarization',
      trigger: 'Auto',
      status: 'Succeeded',
      lastCompressedAt: '2026-09-18T00:00:01.000Z',
      sourceEndSequence: 2,
    } as ContextSummary

    const timeline = buildConversationTimeline(messages, [summary])

    expect(timeline.map(item => item.kind)).toEqual(['message', 'message', 'summary', 'message', 'message'])
  })
})
