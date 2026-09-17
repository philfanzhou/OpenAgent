import { describe, expect, it } from 'vitest'
import { describeTurnMessages, formatDuration, groupInteractions, sourceLabel, statusLabel } from './interactionTimeline'
import type { ConversationRecord, LlmInteraction } from './types'

describe('interaction timeline', () => {
  it('groups interactions by turn trace id and keeps call order', () => {
    const groups = groupInteractions([
      interaction('trace-2', 0, '2026-09-08T00:02:00.000Z'),
      interaction('trace-1', 0, '2026-09-08T00:00:00.000Z'),
      interaction('trace-1', 1, '2026-09-08T00:00:30.000Z'),
    ])

    expect(groups.map(group => group.traceId)).toEqual(['trace-1', 'trace-2'])
    expect(groups[0].items.map(item => item.callIndex)).toEqual([0, 1])
    expect(groups[0].totalTokens).toBe(14)
  })

  it('summarizes the worst status of a mixed turn', () => {
    const groups = groupInteractions([
      interaction('trace-1', 0, '2026-09-08T00:00:00.000Z', 'Succeeded'),
      interaction('trace-1', 1, '2026-09-08T00:00:30.000Z', 'Failed'),
    ])

    expect(groups[0].status).toBe('Mixed')
    expect(statusLabel(groups[0].items[1])).toBe('Failed')
  })

  it('accepts numeric enum values from raw API payloads', () => {
    const base = interaction('t', 0, '2026-09-08T00:00:00.000Z')
    expect(statusLabel({ ...base, status: 2 })).toBe('Cancelled')
    expect(sourceLabel({ ...base, source: 1 })).toBe('压缩')
    expect(sourceLabel({ ...base, source: 0 })).toBe('对话')
  })

  it('describes the message range covered by a turn', () => {
    const conversation = {
      ...baseConversation(),
      messages: [
        message(1, 'trace-1'),
        message(2, 'trace-1'),
        message(3, 'trace-2'),
      ],
    }

    expect(describeTurnMessages(conversation, 'trace-1')).toBe('消息 #1–#2')
    expect(describeTurnMessages(conversation, 'trace-2')).toBe('消息 #3')
    expect(describeTurnMessages(conversation, 'trace-404')).toBe('')
    expect(describeTurnMessages(null, 'trace-1')).toBe('')
  })

  it('formats durations compactly', () => {
    expect(formatDuration(950)).toBe('950ms')
    expect(formatDuration(1250)).toBe('1.3s')
    expect(formatDuration(125_000)).toBe('2m05s')
  })
})

function interaction(traceId: string, callIndex: number, startedAt: string, status: LlmInteraction['status'] = 'Succeeded'): LlmInteraction {
  return {
    interactionId: `${traceId}-${callIndex}`,
    tenantId: 'tenant-1',
    userId: 'user-1',
    conversationId: 'conversation-1',
    traceId,
    agentId: 'agent-1',
    source: 'AgentTurn',
    provider: 'openai',
    apiFormat: 'OpenAIChatCompletions',
    modelId: 'test-model',
    streamed: true,
    callIndex,
    requestJson: null,
    responseJson: null,
    tokenUsage: { promptTokens: 4, completionTokens: 3, totalTokens: 7 },
    status,
    startedAt,
    durationMs: 1000,
  }
}

function baseConversation(): ConversationRecord {
  return {
    conversationId: 'conversation-1',
    tenantId: 'tenant-1',
    userId: 'user-1',
    status: 'Completed',
    createdAt: '2026-09-08T00:00:00.000Z',
    updatedAt: '2026-09-08T00:01:00.000Z',
    lastMessageAt: '2026-09-08T00:01:00.000Z',
    messageCount: 0,
    messages: [],
  }
}

function message(sequence: number, traceId: string) {
  return {
    messageId: `message-${sequence}`,
    sequence,
    role: 'user',
    content: `message-${sequence}`,
    timestamp: '2026-09-08T00:00:00.000Z',
    traceId,
  }
}
