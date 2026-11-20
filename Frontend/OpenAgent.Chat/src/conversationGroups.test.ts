import { describe, expect, it } from 'vitest'
import { groupConversationsByDay } from './conversationGroups'
import type { ConversationRecord } from './types'

function record(id: string, updatedAt: string): ConversationRecord {
  return {
    conversationId: id,
    tenantId: 'tenant-1',
    userId: 'user-1',
    agentId: 'agent-1',
    type: 0,
    status: 'Completed',
    version: 1,
    isDeletedByUser: false,
    createdAt: updatedAt,
    updatedAt,
    lastMessageAt: updatedAt,
    messageCount: 1,
    messages: [],
  } as ConversationRecord
}

// 固定"现在"，避免测试依赖真实时钟。
const NOW = new Date('2026-11-20T09:30:00')

describe('groupConversationsByDay', () => {
  it('keeps last-night conversations (under 24h old) out of 今天', () => {
    // 昨晚 23:59 → 距今不足 24 小时，但日历日属于昨天（旧实现按时间差floor归入今天）。
    const lateNight = record('late', '2026-11-19T23:59:00+08:00')
    const thisMorning = record('morning', '2026-11-20T08:00:00+08:00')

    const groups = groupConversationsByDay([thisMorning, lateNight], NOW)

    expect(groups).toEqual([
      { label: '今天', items: [thisMorning] },
      { label: '昨天', items: [lateNight] },
    ])
  })

  it('labels two calendar days ago as 更早', () => {
    const older = record('older', '2026-11-18T10:00:00+08:00')
    expect(groupConversationsByDay([older], NOW)).toEqual([
      { label: '更早', items: [older] },
    ])
  })

  it('labels yesterday midday as 昨天', () => {
    const yesterday = record('y', '2026-11-19T12:00:00+08:00')
    expect(groupConversationsByDay([yesterday], NOW)).toEqual([
      { label: '昨天', items: [yesterday] },
    ])
  })

  it('groups invalid dates into 更早', () => {
    const broken = record('broken', 'not-a-date')
    expect(groupConversationsByDay([broken], NOW)).toEqual([
      { label: '更早', items: [broken] },
    ])
  })

  it('groups by local calendar day regardless of timezone offset in the timestamp', () => {
    // 用本地时区构造同一时刻的时间戳（序列化为 UTC 后仍指同一本地日历日）。
    const localMorning = new Date(2026, 10, 20, 8, 0)
    const item = record('utc', localMorning.toISOString())
    expect(groupConversationsByDay([item], NOW)).toEqual([
      { label: '今天', items: [item] },
    ])
  })
})
