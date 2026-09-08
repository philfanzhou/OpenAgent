import { describe, expect, it } from 'vitest'
import { createSessionLog, parseSessionLog, SESSION_LOG_FORMAT, SESSION_LOG_VERSION } from './sessionLog'
import type { ConversationRecord } from './types'

describe('session investigation log', () => {
  it('round-trips the complete conversation as a versioned envelope', () => {
    const conversation = createConversation()
    const log = createSessionLog(conversation)

    expect(log.format).toBe(SESSION_LOG_FORMAT)
    expect(log.version).toBe(SESSION_LOG_VERSION)
    expect(log.conversation).toEqual(conversation)
    expect(parseSessionLog(JSON.stringify(log))).toEqual(conversation)
  })

  it('exports the source id and strips local replay markers', () => {
    const conversation = {
      ...createConversation(),
      conversationId: 'replay-local',
      replayOnly: true,
      sourceConversationId: 'conversation-source',
    }

    const imported = parseSessionLog(JSON.stringify(createSessionLog(conversation)))

    expect(imported.conversationId).toBe('conversation-source')
    expect(imported.replayOnly).toBeUndefined()
    expect(imported.sourceConversationId).toBeUndefined()
  })

  it('rejects malformed or unsupported logs', () => {
    expect(() => parseSessionLog('{"format":"other","version":1}')).toThrow('不支持的会话日志格式')
    expect(() => parseSessionLog('not-json')).toThrow('不是有效的 JSON')
  })
})

function createConversation(): ConversationRecord {
  return {
    conversationId: 'conversation-1',
    tenantId: 'tenant-1',
    userId: 'user-1',
    agentId: 'agent-1',
    status: 'Completed',
    createdAt: '2026-09-08T00:00:00.000Z',
    updatedAt: '2026-09-08T00:01:00.000Z',
    lastMessageAt: '2026-09-08T00:01:00.000Z',
    messageCount: 2,
    title: '调查会话',
    messages: [
      {
        messageId: 'message-1', sequence: 1, role: 'user', content: '你好',
        timestamp: '2026-09-08T00:00:00.000Z',
      },
      {
        messageId: 'message-2', sequence: 2, role: 'assistant', content: '你好！',
        timestamp: '2026-09-08T00:01:00.000Z',
        tokenUsage: { promptTokens: 4, completionTokens: 3, totalTokens: 7 },
        modelId: 'test-model',
      },
    ],
  }
}
