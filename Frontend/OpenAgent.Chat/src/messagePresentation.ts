import type { ConversationMessage, MessageFile, ToolActivity } from './types'

export function buildDisplayMessages(messages: ConversationMessage[]): ConversationMessage[] {
  const result: ConversationMessage[] = []
  let pendingReasoning = ''
  let pendingTools: ToolActivity[] = []

  for (const message of messages) {
    const isStoredToolCall = message.role === 'assistant'
      && !message.content
      && !message.files?.length
      && Boolean(message.toolName)
    if (isStoredToolCall) {
      pendingReasoning += message.reasoning || ''
      pendingTools.push({
        name: message.toolName || '工具',
        callId: message.toolCallId,
        arguments: parseToolArguments(message.metadata?.ToolArguments),
      })
      continue
    }

    if (message.role === 'tool') {
      let index = pendingTools.length - 1
      if (message.toolCallId) {
        for (let current = pendingTools.length - 1; current >= 0; current -= 1) {
          if (pendingTools[current]?.callId === message.toolCallId) {
            index = current
            break
          }
        }
      }
      if (index >= 0) pendingTools[index] = { ...pendingTools[index], result: message.content }
      else pendingTools.push({ name: message.toolName || '工具', callId: message.toolCallId, result: message.content })
      continue
    }

    if (message.role === 'assistant') {
      result.push({
        ...message,
        reasoning: `${pendingReasoning}${message.reasoning || ''}` || undefined,
        toolActivities: [...pendingTools, ...(message.toolActivities || [])],
      })
      pendingReasoning = ''
      pendingTools = []
      continue
    }

    result.push(message)
  }

  if (pendingReasoning || pendingTools.length) {
    result.push({
      messageId: 'pending-agent-process',
      sequence: Number.MAX_SAFE_INTEGER,
      role: 'assistant',
      content: '',
      timestamp: new Date().toISOString(),
      reasoning: pendingReasoning || undefined,
      toolActivities: pendingTools,
    })
  }
  return result
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

  if (name === 'run_skill_script') return { kind: 'SKILL 脚本', displayName: '执行 Skill 脚本' }
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
