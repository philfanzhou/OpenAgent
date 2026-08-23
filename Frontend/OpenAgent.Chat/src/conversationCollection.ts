import type { ConversationRecord } from './types'

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
