import { describe, expect, it } from 'vitest'
import { mergeConversationRecords, replaceConversationRecord, selectionMatchesConversation } from './conversationCollection'
import type { ContextSummary, ConversationMessage, ConversationRecord } from './types'

function conversation(conversationId: string, status: ConversationRecord['status'], messages?: ConversationMessage[]): ConversationRecord {
  return {
    conversationId,
    tenantId: 'tenant-1',
    userId: 'user-1',
    agentId: 'agent-1',
    status,
    createdAt: '2026-08-17T00:00:00.000Z',
    updatedAt: '2026-08-17T00:00:00.000Z',
    lastMessageAt: '2026-08-17T00:00:00.000Z',
    messageCount: messages?.length || 0,
    title: conversationId,
    messages,
  }
}

function message(content: string): ConversationMessage {
  return {
    messageId: content,
    sequence: 1,
    role: 'assistant',
    content,
    timestamp: '2026-08-17T00:00:00.000Z',
  }
}

describe('conversation collection', () => {
  it('preserves live messages and status when a stale list refresh arrives', () => {
    const live = conversation('conversation-a', 'Running', [message('latest streamed text')])
    live.messageCount = 2
    const staleSummary = conversation('conversation-a', 'Completed')

    const result = mergeConversationRecords([live], [staleSummary], new Set(['conversation-a']))

    expect(result[0]).toBe(live)
    expect(result[0]?.status).toBe('Running')
    expect(result[0]?.messages?.[0]?.content).toBe('latest streamed text')
    expect(result[0]?.messageCount).toBe(2)
  })

  it('retains a local streaming conversation missing from the persisted list', () => {
    const live = conversation('temporary-a', 'Running', [message('partial')])

    const result = mergeConversationRecords([live], [], new Set(['temporary-a']))

    expect(result).toEqual([live])
  })

  it('preserves loaded compaction details when list metadata refreshes', () => {
    const detail = conversation('conversation-a', 'Completed')
    detail.contextSummaries = [{
      compressionId: 'compression-1',
      strategy: 'summarization',
      trigger: 'Automatic',
      status: 'Succeeded',
      summary: 'Retained context.',
      lastCompressedAt: '2026-08-19T00:00:00Z',
      compressedMessageCount: 4,
      originalStartSequence: 1,
      originalEndSequence: 4,
      originalTokenCount: 40,
      tokenCount: 20,
      originalHistoryRestored: false,
      sourceEndSequence: 4,
    } satisfies ContextSummary]

    const result = mergeConversationRecords(
      [detail],
      [conversation('conversation-a', 'Completed')],
      new Set(),
      'conversation-a',
    )

    expect(result[0]?.contextSummaries?.[0]?.summary).toBe('Retained context.')
  })

  it('replaces a completed background conversation without matching another selected view', () => {
    const persisted = conversation('conversation-a', 'Completed', [message('final answer')])
    const result = replaceConversationRecord(
      [conversation('conversation-a', 'Running'), conversation('conversation-b', 'Completed')],
      persisted,
    )

    expect(result[0]).toBe(persisted)
    expect(selectionMatchesConversation('conversation-b', persisted.conversationId)).toBe(false)
    expect(result[0]?.messages?.[0]?.content).toBe('final answer')
  })

  it('matches a temporary selection when the server assigns a persisted id', () => {
    expect(selectionMatchesConversation('temporary-a', 'persisted-a', 'temporary-a')).toBe(true)
  })
})
