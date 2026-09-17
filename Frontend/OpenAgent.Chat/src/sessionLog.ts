import type { ConversationRecord, LlmInteraction } from './types'

export const SESSION_LOG_FORMAT = 'openagent-session-log'
export const SESSION_LOG_VERSION = 2

export interface SessionLog {
  format: typeof SESSION_LOG_FORMAT
  version: typeof SESSION_LOG_VERSION
  exportedAt: string
  conversation: ConversationRecord
  /** 后端记录的大模型交互日志（已脱敏）；v1 日志或旧会话可能为空数组。 */
  interactions: LlmInteraction[]
}

export interface ParsedSessionLog {
  conversation: ConversationRecord
  interactions: LlmInteraction[]
}

export function createSessionLog(conversation: ConversationRecord, interactions: LlmInteraction[] = []): SessionLog {
  const exportedConversation = JSON.parse(JSON.stringify(conversation)) as ConversationRecord
  if (exportedConversation.replayOnly && exportedConversation.sourceConversationId) {
    exportedConversation.conversationId = exportedConversation.sourceConversationId
  }
  exportedConversation.messages ||= []
  delete exportedConversation.replayOnly
  delete exportedConversation.sourceConversationId
  delete exportedConversation.interactions
  return {
    format: SESSION_LOG_FORMAT,
    version: SESSION_LOG_VERSION,
    exportedAt: new Date().toISOString(),
    conversation: exportedConversation,
    interactions: JSON.parse(JSON.stringify(interactions)) as LlmInteraction[],
  }
}

export function parseSessionLog(text: string): ParsedSessionLog {
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    throw new Error('会话日志不是有效的 JSON 文件')
  }

  if (!isRecord(value)
    || value.format !== SESSION_LOG_FORMAT
    || (value.version !== 2 && value.version !== 1)
    || !isConversation(value.conversation)) {
    throw new Error('不支持的会话日志格式')
  }

  const conversation = JSON.parse(JSON.stringify(value.conversation)) as ConversationRecord
  delete conversation.replayOnly
  delete conversation.sourceConversationId
  delete conversation.interactions
  const interactions = value.version === 2 && Array.isArray(value.interactions)
    ? value.interactions.filter(isInteraction) as LlmInteraction[]
    : []
  return { conversation, interactions }
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

function isInteraction(value: unknown): value is LlmInteraction {
  return isRecord(value)
    && typeof value.interactionId === 'string'
    && typeof value.traceId === 'string'
    && typeof value.modelId === 'string'
    && typeof value.startedAt === 'string'
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
