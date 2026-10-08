import { getTenantId, request } from '../../shared/api/http'
import { randomUuid } from '../../shared/browserCrypto'
import type { ContextSummary, ConversationMessage, ConversationRecord, MessageFile, MessageFileMetadata } from '../../shared/contracts/types'

function normalizeConversation(record: ConversationRecord): ConversationRecord {
  return {
    ...record,
    // 附件清单与思考链已由后端以强类型 metadata 下发，这里仅做顶层 UI 投影。
    messages: record.messages?.map(message => {
      const { files, reasoning } = message.metadata ?? {}
      if (!files?.length && !reasoning) return message
      return {
        ...message,
        ...(files?.length ? { files: files.map(toMessageFile) } : {}),
        ...(reasoning ? { reasoning } : {}),
      }
    }),
  }
}

function toMessageFile(file: MessageFileMetadata): MessageFile {
  return {
    fileId: file.fileId,
    fileName: file.fileName,
    mediaType: file.mediaType,
    length: file.length,
    ...(file.objectKey ? { objectKey: file.objectKey } : {}),
  }
}

export const api = {
  listConversations(): Promise<ConversationRecord[]> {
    return request<ConversationRecord[]>('/api/v1/agent/conversations?skip=0&take=1000')
  },

  getConversation(id: string): Promise<ConversationRecord> {
    return request<ConversationRecord>(`/api/v1/agent/conversations/${encodeURIComponent(id)}`).then(normalizeConversation)
  },

  deleteConversation(id: string): Promise<void> {
    return request<void>(`/api/v1/agent/conversations/${encodeURIComponent(id)}`, { method: 'DELETE' })
  },

  compactConversation(id: string, llmProfileId: string): Promise<ContextSummary> {
    return request<ContextSummary>(`/api/v1/agent/conversations/${encodeURIComponent(id)}/compact?llmProfileId=${encodeURIComponent(llmProfileId)}`, { method: 'POST' })
  }
}

export function makeLocalConversation(agentId: string, message: string): ConversationRecord {
  const now = new Date().toISOString()
  const userMessage: ConversationMessage = {
    messageId: randomUuid(),
    sequence: 1,
    role: 'user',
    content: message,
    timestamp: now,
  }
  return {
    conversationId: randomUuid(),
    tenantId: getTenantId(),
    userId: 'local',
    agentId,
    type: 0,
    status: 'Running',
    version: 1,
    isDeletedByUser: false,
    createdAt: now,
    updatedAt: now,
    lastMessageAt: now,
    messageCount: 1,
    title: message.slice(0, 40),
    messages: [userMessage],
  }
}
