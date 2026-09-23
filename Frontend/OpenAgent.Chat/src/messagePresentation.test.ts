import { describe, expect, it } from 'vitest'
import { buildConversationTimeline, buildDisplayMessages, mergeAssistantSnapshot, parsePlanSnapshot } from './messagePresentation'
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

  it('backfills a callId-less tool result onto the open call activity', () => {
    // 部分提供方/旧数据不带 callId：调用行（有参无果）与结果行（有果无参）
    // 必须合成一条活动，而不是拆成两条、结果行退化为“工具”占位。
    const rows = [
      message({ sequence: 1, role: 'user', content: 'hi' }),
      message({
        sequence: 2, role: 'assistant', content: '',
        toolName: 'read_file', metadata: { toolArguments: '{"fileId":"f-1"}' },
      }),
      message({ sequence: 3, role: 'tool', content: 'file body' }),
    ]

    const display = buildDisplayMessages(rows)

    const assistant = display[1]!
    expect(assistant.toolActivities).toHaveLength(1)
    expect(assistant.toolActivities?.[0]).toMatchObject({
      name: 'read_file',
      result: 'file body',
    })
    expect(assistant.toolActivities?.[0]?.arguments).toEqual({ fileId: 'f-1' })
  })

  it('carries the last source sequence on a merged display assistant', () => {
    // 乐观消息的序号从内存最大序号推算：折叠组若停在首行序号（2），推算出的
    // 下一行会小于服务端真实行号（5），停止/完成后的历史合并随之错位。
    const rows = [
      message({ sequence: 1, role: 'user', content: 'hi' }),
      message({ sequence: 2, role: 'assistant', content: '', toolCallId: 'call-1', toolName: 'search' }),
      message({ sequence: 3, role: 'tool', content: 'hits', toolCallId: 'call-1', toolName: 'search' }),
      message({ sequence: 4, role: 'assistant', content: 'answer' }),
    ]

    const display = buildDisplayMessages(rows)

    expect(display.map(item => item.role)).toEqual(['user', 'assistant'])
    expect(display[1]!.sequence).toBe(4)
  })

  it('skips persisted compaction summary rows from display messages', () => {
    // 自动压缩会把摘要落成 role="summary" 的消息行；对用户它由 contextSummaries
    // 的压缩分隔条呈现，直接展示会把同一段摘要渲染成无样式普通消息并拆散
    // assistant 组。
    const rows = [
      message({ sequence: 1, role: 'user', content: 'question' }),
      message({ sequence: 2, role: 'summary', content: 'Earlier conversation summary' }),
      message({ sequence: 3, role: 'assistant', content: 'answer' }),
    ]

    const display = buildDisplayMessages(rows)

    expect(display.map(item => `${item.role}:${item.content}`)).toEqual([
      'user:question',
      'assistant:answer',
    ])
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

describe('plan snapshot', () => {
  const camelPayload = JSON.stringify({
    plan: [
      { step: 'collect', status: 'completed' },
      { step: 'analyze', status: 'in_progress' },
      { step: 'report', status: 'pending' },
    ],
    completed: 1,
    total: 3,
  })

  it('parses camelCase plan snapshots from live events', () => {
    const snapshot = parsePlanSnapshot(camelPayload)
    expect(snapshot?.total).toBe(3)
    expect(snapshot?.completed).toBe(1)
    expect(snapshot?.plan.map(item => item.status)).toEqual(['completed', 'in_progress', 'pending'])
  })

  it('parses legacy PascalCase snapshots from stored history rows', () => {
    const snapshot = parsePlanSnapshot(JSON.stringify({
      plan: [{ Step: 'collect', Status: 'in_progress' }],
    }))
    expect(snapshot?.plan).toEqual([{ step: 'collect', status: 'in_progress' }])
    expect(snapshot?.completed).toBe(0)
    expect(snapshot?.total).toBe(1)
  })

  it('returns undefined for malformed or plan-less payloads', () => {
    expect(parsePlanSnapshot(undefined)).toBeUndefined()
    expect(parsePlanSnapshot('not json')).toBeUndefined()
    expect(parsePlanSnapshot(JSON.stringify({ plan: [] }))).toBeUndefined()
    expect(parsePlanSnapshot(JSON.stringify({ total: 3 }))).toBeUndefined()
  })

  it('projects update_plan history tool rows onto the assistant plan', () => {
    const rows = [
      message({ sequence: 1, role: 'user', content: 'hi' }),
      message({ sequence: 2, role: 'assistant', content: '', toolCallId: 'call-1', toolName: 'update_plan' }),
      message({ sequence: 3, role: 'tool', content: camelPayload, toolCallId: 'call-1', toolName: 'update_plan' }),
      message({ sequence: 4, role: 'assistant', content: 'done' }),
    ]

    const display = buildDisplayMessages(rows)
    const assistant = display[1]!
    expect(assistant.plan?.total).toBe(3)
    expect(assistant.plan?.plan[1]).toEqual({ step: 'analyze', status: 'in_progress' })
  })

  it('keeps the streamed plan when the persisted history replaces the optimistic message', () => {
    const history = [
      message({ sequence: 1, role: 'user', content: 'hi' }),
      message({ sequence: 2, role: 'assistant', content: 'final answer' }),
    ]
    const snapshot = message({ sequence: 2, role: 'assistant', content: '' })
    snapshot.plan = parsePlanSnapshot(camelPayload)

    const merged = mergeAssistantSnapshot(history, snapshot)
    const assistant = merged[1]!
    expect(assistant.plan?.total).toBe(3)
  })

  it('keeps the stored plan when the streamed snapshot carries none', () => {
    const storedRow = message({ sequence: 2, role: 'assistant', content: 'final answer' })
    storedRow.plan = parsePlanSnapshot(camelPayload)
    const history = [
      message({ sequence: 1, role: 'user', content: 'hi' }),
      storedRow,
    ]
    const snapshot = message({ sequence: 2, role: 'assistant', content: '' })

    const merged = mergeAssistantSnapshot(history, snapshot)
    expect(merged[1]!.plan?.total).toBe(3)
  })
})
