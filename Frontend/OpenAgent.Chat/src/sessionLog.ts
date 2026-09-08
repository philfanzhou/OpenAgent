import type { ConversationRecord } from './types'

export const SESSION_LOG_FORMAT = 'openagent-session-log'
export const SESSION_LOG_VERSION = 1

export interface SessionLog {
  format: typeof SESSION_LOG_FORMAT
  version: typeof SESSION_LOG_VERSION
  exportedAt: string
  conversation: ConversationRecord
}

export function createSessionLog(conversation: ConversationRecord): SessionLog {
  const exportedConversation = JSON.parse(JSON.stringify(conversation)) as ConversationRecord
  if (exportedConversation.replayOnly && exportedConversation.sourceConversationId) {
    exportedConversation.conversationId = exportedConversation.sourceConversationId
  }
  exportedConversation.messages ||= []
  delete exportedConversation.replayOnly
  delete exportedConversation.sourceConversationId
  return {
    format: SESSION_LOG_FORMAT,
    version: SESSION_LOG_VERSION,
    exportedAt: new Date().toISOString(),
    conversation: exportedConversation,
  }
}

export function parseSessionLog(text: string): ConversationRecord {
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    throw new Error('会话日志不是有效的 JSON 文件')
  }

  if (!isRecord(value)
    || value.format !== SESSION_LOG_FORMAT
    || value.version !== SESSION_LOG_VERSION
    || !isConversation(value.conversation)) {
    throw new Error('不支持的会话日志格式')
  }

  const conversation = JSON.parse(JSON.stringify(value.conversation)) as ConversationRecord
  delete conversation.replayOnly
  delete conversation.sourceConversationId
  return conversation
}

function isConversation(value: unknown): value is ConversationRecord {
  if (!isRecord(value)
    || typeof value.conversationId !== 'string'
    || typeof value.tenantId !== 'string'
    || typeof value.userId !== 'string'
    || !Array.isArray(value.messages)) {
    return false
  }

  return value.messages.every(message => isRecord(message)
    && typeof message.messageId === 'string'
    && typeof message.sequence === 'number'
    && typeof message.role === 'string'
    && typeof message.content === 'string')
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
