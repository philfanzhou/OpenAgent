import type { ConversationRecord } from './types'
import type { ConversationMessage } from './types'

/**
 * The server can return a stale first-turn snapshot while a cancelled stream
 * is still being finalized. Keep optimistic user messages until the durable
 * snapshot contains the same turn, otherwise the user bubble disappears and
 * only comes back after the next full reload.
 */
export function mergeOptimisticUserMessages(
  persisted: ConversationMessage[],
  optimistic: ConversationMessage[] = [],
): ConversationMessage[] {
  const merged = [...persisted]
  for (const message of optimistic.filter(item => item.role === 'user')) {
    const existing = merged.find(item =>
      item.role === 'user'
      && (item.messageId === message.messageId
        || (item.sequence === message.sequence && item.content === message.content)))
    if (existing) {
      if (!existing.files?.length && message.files?.length) existing.files = message.files
      continue
    }

    const insertAt = merged.findIndex(item => (item.sequence || 0) > (message.sequence || 0))
    if (insertAt < 0) merged.push(message)
    else merged.splice(insertAt, 0, message)
  }
  return merged
}

export function mergeConversationRecords(
  current: ConversationRecord[],
  refreshed: ConversationRecord[],
  streamingConversationIds: ReadonlySet<string>,
  selectedConversationId?: string,
): ConversationRecord[] {
  const existingById = new Map(current.map(item => [item.conversationId, item]))
  const merged = refreshed.map(summary => {
    const existing = existingById.get(summary.conversationId)
    if (!existing) return summary
    existingById.delete(summary.conversationId)
    const messages = existing.messages
    const contextSummaries = existing.contextSummaries
    if (streamingConversationIds.has(summary.conversationId)) {
      existing.tenantId = summary.tenantId
      existing.userId = summary.userId
      existing.agentId = summary.agentId || existing.agentId
      existing.title = summary.title || existing.title
      existing.createdAt = summary.createdAt
    } else {
      Object.assign(existing, summary)
    }
    if (messages?.length) existing.messages = messages
    if (contextSummaries?.length) existing.contextSummaries = contextSummaries
    return existing
  })
  const retained = Array.from(existingById.values()).filter(item =>
    streamingConversationIds.has(item.conversationId) || selectedConversationId === item.conversationId)
  return [...merged, ...retained]
}

export function replaceConversationRecord(
  conversations: ConversationRecord[],
  detail: ConversationRecord,
  previousConversationId = detail.conversationId,
): ConversationRecord[] {
  const index = conversations.findIndex(item =>
    item.conversationId === previousConversationId || item.conversationId === detail.conversationId)
  if (index < 0) return [detail, ...conversations]
  const replaced = [...conversations]
  replaced[index] = detail
  return replaced
}

export function selectionMatchesConversation(
  selectedConversationId: string | undefined,
  detailConversationId: string,
  previousConversationId = detailConversationId,
): boolean {
  return selectedConversationId === previousConversationId || selectedConversationId === detailConversationId
}
