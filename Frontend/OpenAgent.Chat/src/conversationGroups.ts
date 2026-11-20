import type { ConversationRecord } from './types'

export type ConversationGroupLabel = '今天' | '昨天' | '更早'

export interface ConversationGroup {
  label: ConversationGroupLabel
  items: ConversationRecord[]
}

function resolveDate(item: ConversationRecord): Date | null {
  const date = new Date(item.updatedAt || item.lastMessageAt || item.createdAt)
  return Number.isNaN(date.getTime()) ? null : date
}

function isSameDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}

/**
 * 按"本地日历日"分组（今天/昨天/更早），而不是按"距满 24 小时"：
 * 昨晚 23:59 的会话在今天早上看，时间差不足 24 小时，但仍属于昨天。
 * 分组顺序与传入顺序一致（列表已按最近更新排序，"今天"自然排最前）。
 */
export function groupConversationsByDay(items: ConversationRecord[], now = new Date()): ConversationGroup[] {
  const yesterday = new Date(now)
  yesterday.setDate(now.getDate() - 1)
  const groups = new Map<ConversationGroupLabel, ConversationRecord[]>()
  for (const item of items) {
    const date = resolveDate(item)
    const label: ConversationGroupLabel = date === null
      ? '更早'
      : isSameDay(date, now)
        ? '今天'
        : isSameDay(date, yesterday)
          ? '昨天'
          : '更早'
    const bucket = groups.get(label)
    if (bucket) {
      bucket.push(item)
    } else {
      groups.set(label, [item])
    }
  }
  return Array.from(groups, ([label, groupItems]) => ({ label, items: groupItems }))
}
