import type { ConversationRecord, LlmInteraction } from './types'

export interface InteractionGroup {
  /** 轮次追溯键；无法归组（缺失 traceId）的日志归入空串桶。 */
  traceId: string
  items: LlmInteraction[]
  startedAt: string
  status: 'Succeeded' | 'Failed' | 'Cancelled' | 'Mixed'
  totalTokens: number
}

/** 按轮次（traceId）分组，组间按首次交互时间升序，组内保持调用序。 */
export function groupInteractions(interactions: LlmInteraction[]): InteractionGroup[] {
  const groups = new Map<string, LlmInteraction[]>()
  for (const item of [...interactions].sort((a, b) => a.startedAt.localeCompare(b.startedAt))) {
    const key = item.traceId || ''
    const bucket = groups.get(key)
    if (bucket) bucket.push(item)
    else groups.set(key, [item])
  }

  return Array.from(groups.entries()).map(([traceId, items]) => ({
    traceId,
    items,
    startedAt: items[0]?.startedAt || '',
    status: summarizeStatus(items),
    totalTokens: items.reduce((sum, item) => sum + (item.tokenUsage?.totalTokens || 0), 0),
  }))
}

function summarizeStatus(items: LlmInteraction[]): InteractionGroup['status'] {
  const statuses = new Set(items.map(item => statusLabel(item)))
  if (statuses.size === 1) return statuses.values().next().value as InteractionGroup['status']
  return 'Mixed'
}

export function statusLabel(item: LlmInteraction): 'Succeeded' | 'Failed' | 'Cancelled' {
  const value = typeof item.status === 'number' ? item.status : statusNameToValue(item.status)
  if (value === 1) return 'Failed'
  if (value === 2) return 'Cancelled'
  return 'Succeeded'
}

export function sourceLabel(item: LlmInteraction): string {
  const value = typeof item.source === 'number' ? item.source : item.source === 'Compaction' ? 1 : 0
  return value === 1 ? '压缩' : '对话'
}

function statusNameToValue(status: string): number {
  if (status === 'Failed') return 1
  if (status === 'Cancelled') return 2
  return 0
}

/**
 * 轮次摘要：给出该 traceId 覆盖的消息序号区间（如 "3–5"），
 * 用于把交互轮次和前端消息时间线对齐。
 */
export function describeTurnMessages(conversation: ConversationRecord | null | undefined, traceId: string): string {
  if (!conversation?.messages || !traceId) return ''
  const sequences = conversation.messages
    .filter(message => message.traceId === traceId)
    .map(message => message.sequence)
    .sort((a, b) => a - b)
  if (!sequences.length) return ''
  const first = sequences[0]
  const last = sequences[sequences.length - 1]
  return first === last ? `消息 #${first}` : `消息 #${first}–#${last}`
}

export function formatDuration(durationMs: number): string {
  if (durationMs < 1000) return `${durationMs}ms`
  if (durationMs < 60_000) return `${(durationMs / 1000).toFixed(1)}s`
  const minutes = Math.floor(durationMs / 60_000)
  const seconds = Math.round((durationMs % 60_000) / 1000)
  return `${minutes}m${seconds.toString().padStart(2, '0')}s`
}

export function prettyJson(value: string | null | undefined): string {
  if (!value) return ''
  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}
