import type { ConversationMessage, MessageFile, ToolActivity } from './types'

export function buildDisplayMessages(messages: ConversationMessage[]): ConversationMessage[] {
  const result: ConversationMessage[] = []
  let assistant: ConversationMessage | undefined

  for (const message of messages) {
    if (message.role === 'assistant') {
      assistant = mergeAssistantMessage(assistant, message)
      continue
    }

    if (message.role === 'tool') {
      assistant ||= createAssistantMessage(message)
      assistant.toolActivities = mergeToolActivity(assistant.toolActivities, {
        name: message.toolName || '工具',
        callId: message.toolCallId,
        result: message.content,
      })
      continue
    }

    if (assistant) result.push(assistant)
    assistant = undefined
    result.push(message)
  }

  if (assistant) result.push(assistant)
  return result
}

/** Preserve streamed-only details when a completed, failed, or cancelled request is replaced by history. */
export function mergeAssistantSnapshot(
  messages: ConversationMessage[],
  snapshot: ConversationMessage,
): ConversationMessage[] {
  const merged = buildDisplayMessages(messages)
  let assistantIndex = -1
  for (let index = merged.length - 1; index >= 0; index -= 1) {
    if (merged[index]?.role === 'user') break
    if (merged[index]?.role === 'assistant') {
      assistantIndex = index
      break
    }
  }
  if (assistantIndex < 0) {
    return [
      ...merged,
      { ...snapshot, toolActivities: snapshot.toolActivities?.map(tool => ({ ...tool })) },
    ]
  }

  const stored = merged[assistantIndex]!
  merged[assistantIndex] = {
    ...stored,
    content: preferCompleteText(stored.content, snapshot.content),
    reasoning: preferCompleteText(stored.reasoning, snapshot.reasoning) || undefined,
    toolActivities: mergeToolActivities(stored.toolActivities, snapshot.toolActivities),
    files: stored.files?.length ? stored.files : snapshot.files,
    error: snapshot.error || stored.error,
  }
  return merged
}

function mergeAssistantMessage(
  current: ConversationMessage | undefined,
  message: ConversationMessage,
): ConversationMessage {
  const merged = current
    ? {
        ...current,
        content: appendText(current.content, message.content),
        reasoning: appendText(current.reasoning, message.reasoning) || undefined,
        files: current.files?.length || message.files?.length
          ? [...(current.files || []), ...(message.files || [])]
          : undefined,
        error: message.error || current.error,
      }
    : {
        ...message,
        toolActivities: [],
        files: message.files ? [...message.files] : undefined,
      }

  if (message.toolName) {
    merged.toolActivities = mergeToolActivity(merged.toolActivities, {
      name: message.toolName,
      callId: message.toolCallId,
      arguments: parseToolArguments(message.metadata?.ToolArguments),
    })
  }
  merged.toolActivities = mergeToolActivities(merged.toolActivities, message.toolActivities)
  return merged
}

function createAssistantMessage(message: ConversationMessage): ConversationMessage {
  return {
    messageId: `assistant-${message.messageId}`,
    sequence: message.sequence,
    role: 'assistant',
    content: '',
    timestamp: message.timestamp,
    toolActivities: [],
  }
}

function mergeToolActivities(
  current: ToolActivity[] | undefined,
  incoming: ToolActivity[] | undefined,
): ToolActivity[] {
  let merged = current?.map(tool => ({ ...tool })) || []
  for (const tool of incoming || []) merged = mergeToolActivity(merged, tool)
  return merged
}

function mergeToolActivity(current: ToolActivity[] | undefined, incoming: ToolActivity): ToolActivity[] {
  const merged = current ? [...current] : []
  const index = incoming.callId
    ? merged.findIndex(tool => tool.callId === incoming.callId)
    : -1
  if (index < 0) {
    merged.push({ ...incoming })
    return merged
  }

  const existing = merged[index]!
  merged[index] = {
    name: existing.name === '工具' && incoming.name !== '工具' ? incoming.name : existing.name,
    callId: existing.callId || incoming.callId,
    arguments: incoming.arguments ?? existing.arguments,
    result: incoming.result ?? existing.result,
  }
  return merged
}

function appendText(current?: string, incoming?: string): string {
  if (!current) return incoming || ''
  if (!incoming) return current
  return `${current}${incoming}`
}

function preferCompleteText(stored?: string, streamed?: string): string {
  if (!stored) return streamed || ''
  if (!streamed || stored.includes(streamed)) return stored
  if (streamed.includes(stored)) return streamed
  return stored
}

export function parseToolArguments(json?: string): unknown {
  if (!json) return undefined
  try { return JSON.parse(json) } catch { return json }
}

export function toolArgumentsText(tool: ToolActivity): string | undefined {
  if (tool.arguments == null) return undefined
  if (typeof tool.arguments === 'string') return tool.arguments
  try { return JSON.stringify(tool.arguments, null, 2) } catch { return String(tool.arguments) }
}

export function toolPresentation(name: string): { kind: string; displayName: string } {
  if (name.startsWith('mcp__')) {
    const parts = name.split('__').filter(Boolean)
    const server = parts[1] || 'server'
    const tool = parts.slice(2).join(' / ') || 'tool'
    return { kind: 'MCP', displayName: `${server} / ${tool}` }
  }

  if (name === 'load_skill') return { kind: 'SKILL', displayName: '加载 Skill 指令' }
  if (name === 'read_skill_resource') return { kind: 'SKILL', displayName: '读取 Skill 资源' }
  return { kind: '工具', displayName: name }
}

export function formatFileSize(size: number): string {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${Math.ceil(size / 1024)} KB`
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}

export function fileLabel(file: MessageFile): string {
  const extension = file.fileName.split('.').pop()?.trim().toUpperCase()
  if (extension && extension !== file.fileName.toUpperCase() && extension.length <= 5) return extension
  if (file.mediaType.startsWith('image/')) return 'IMG'
  if (file.mediaType === 'application/pdf') return 'PDF'
  return 'FILE'
}
