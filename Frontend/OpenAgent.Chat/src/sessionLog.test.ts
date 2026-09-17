import { describe, expect, it } from 'vitest'
import { createSessionLog, parseSessionLog, SESSION_LOG_FORMAT, SESSION_LOG_VERSION } from './sessionLog'
import type { ConversationRecord, LlmInteraction } from './types'

describe('session investigation log', () => {
  it('round-trips the conversation and interaction logs as a versioned envelope', () => {
    const conversation = createConversation()
    const interactions = [createInteraction('trace-1'), createInteraction('trace-1', 1)]
    const log = createSessionLog(conversation, interactions)

    expect(log.format).toBe(SESSION_LOG_FORMAT)
    expect(log.version).toBe(SESSION_LOG_VERSION)
    expect(log.conversation).toEqual(conversation)
    expect(log.interactions).toEqual(interactions)

    const parsed = parseSessionLog(JSON.stringify(log))
    expect(parsed.conversation).toEqual(conversation)
    expect(parsed.interactions).toEqual(interactions)
  })

  it('still imports version 1 logs without interactions', () => {
    const v1 = JSON.stringify({
      format: SESSION_LOG_FORMAT,
      version: 1,
      exportedAt: '2026-09-08T00:00:00.000Z',
      conversation: createConversation(),
    })

    const parsed = parseSessionLog(v1)

    expect(parsed.conversation.conversationId).toBe('conversation-1')
    expect(parsed.interactions).toEqual([])
  })

  it('exports the source id and strips local replay markers', () => {
    const conversation = {
      ...createConversation(),
      conversationId: 'replay-local',
      replayOnly: true,
      sourceConversationId: 'conversation-source',
    }

    const parsed = parseSessionLog(JSON.stringify(createSessionLog(conversation, [])))

    expect(parsed.conversation.conversationId).toBe('conversation-source')
    expect(parsed.conversation.replayOnly).toBeUndefined()
    expect(parsed.conversation.sourceConversationId).toBeUndefined()
    expect(parsed.conversation.interactions).toBeUndefined()
  })

  it('rejects malformed or unsupported logs', () => {
    expect(() => parseSessionLog('{"format":"other","version":2}')).toThrow('不支持的会话日志格式')
    expect(() => parseSessionLog('{"format":"openagent-session-log","version":3,"conversation":{}}')).toThrow('不支持的会话日志格式')
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
        traceId: 'trace-1',
      },
      {
        messageId: 'message-2', sequence: 2, role: 'assistant', content: '你好！',
        timestamp: '2026-09-08T00:01:00.000Z',
        traceId: 'trace-1',
        tokenUsage: { promptTokens: 4, completionTokens: 3, totalTokens: 7 },
        modelId: 'test-model',
      },
    ],
  }
}

function createInteraction(traceId: string, callIndex = 0): LlmInteraction {
  return {
    interactionId: `interaction-${traceId}-${callIndex}`,
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
    requestJson: '{"messages":[{"role":"user","text":"你好"}]}',
    responseJson: '{"messages":[{"role":"assistant","text":"你好！"}]}',
    tokenUsage: { promptTokens: 4, completionTokens: 3, totalTokens: 7 },
    status: 'Succeeded',
    startedAt: '2026-09-08T00:00:30.000Z',
    durationMs: 1200,
  }
}
