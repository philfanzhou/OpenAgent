import type { ContextSummary, ConversationMessage, MessageFile, PlanSnapshot, PlanStep, ProcessActivity, ToolActivity } from './types'

export type ConversationTimelineItem =
  | { kind: 'message'; message: ConversationMessage }
  | { kind: 'summary'; summary: ContextSummary }

export function buildConversationTimeline(
  messages: ConversationMessage[],
  summaries: ContextSummary[],
): ConversationTimelineItem[] {
  // 序号缺失时按 0 处理：NaN 比较结果会让整个排序顺序错乱。
  const orderedMessages = [...messages].sort((left, right) => (left.sequence || 0) - (right.sequence || 0))
  const orderedSummaries = summaries
    .map(summary => ({ summary, boundary: summaryBoundary(summary, orderedMessages) }))
    .sort((left, right) => left.boundary - right.boundary
      || Date.parse(left.summary.lastCompressedAt) - Date.parse(right.summary.lastCompressedAt))
  const result: ConversationTimelineItem[] = []
  let messageIndex = 0

  for (const entry of orderedSummaries) {
    const segment: ConversationMessage[] = []
    while (messageIndex < orderedMessages.length
      && orderedMessages[messageIndex]!.sequence <= entry.boundary) {
      segment.push(orderedMessages[messageIndex]!)
      messageIndex += 1
    }
    result.push(...buildDisplayMessages(segment).map(message => ({ kind: 'message' as const, message })))
    result.push({ kind: 'summary', summary: entry.summary })
  }

  result.push(...buildDisplayMessages(orderedMessages.slice(messageIndex))
    .map(message => ({ kind: 'message' as const, message })))
  return result
}

export function buildDisplayMessages(messages: ConversationMessage[]): ConversationMessage[] {
  const result: ConversationMessage[] = []
  let assistant: ConversationMessage | undefined

  for (const message of messages) {
    // 压缩摘要行是模型上下文的产物，对用户由 contextSummaries 的压缩分隔条呈现；
    // 这里跳过可避免同一段摘要以无样式普通消息重复渲染、并把 assistant 组拆散。
    if (message.role === 'summary') continue

    if (message.role === 'assistant') {
      assistant = mergeAssistantMessage(assistant, message)
      continue
    }

    if (message.role === 'tool') {
      assistant ||= createAssistantMessage(message)
      // 合并组携带最后一行的序号：乐观消息的序号基于内存消息推算，折叠组若停在
      // 首行序号，推算值会小于服务端真实行号，停止/完成后的历史合并就会错位。
      assistant.sequence = message.sequence
      mergeToolIntoAssistant(assistant, {
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
  const snapshotProcesses = mergeAssistantMessage(undefined, snapshot).processActivities
  merged[assistantIndex] = {
    ...stored,
    content: preferCompleteText(stored.content, snapshot.content),
    reasoning: preferCompleteText(stored.reasoning, snapshot.reasoning) || undefined,
    toolActivities: mergeToolActivities(stored.toolActivities, snapshot.toolActivities),
    processActivities: mergeProcessActivities(stored.processActivities, snapshotProcesses),
    plan: snapshot.plan || stored.plan,
    files: stored.files?.length ? stored.files : snapshot.files,
    tokenUsage: snapshot.tokenUsage || stored.tokenUsage,
    modelId: snapshot.modelId || stored.modelId,
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
        // 携带末行序号：乐观消息序号从内存最大序号推算，合并组停在首行序号
        // 会让推算值落后于服务端行号，历史合并时错插/漏配（见 buildDisplayMessages）。
        sequence: message.sequence,
        toolCallId: message.toolCallId,
        toolName: message.toolName,
        reasoning: appendText(current.reasoning, message.reasoning) || undefined,
        files: current.files?.length || message.files?.length
          ? [...(current.files || []), ...(message.files || [])]
          : undefined,
        processActivities: cloneProcessActivities(current.processActivities),
        tokenUsage: message.tokenUsage || current.tokenUsage,
        modelId: message.modelId || current.modelId,
        error: message.error || current.error,
      }
    : {
        ...message,
        toolActivities: [],
        processActivities: [],
        files: message.files ? [...message.files] : undefined,
      }

  const hasOrderedProcesses = Boolean(message.processActivities?.length)
  if (hasOrderedProcesses) {
    merged.processActivities = mergeProcessActivities(
      merged.processActivities,
      message.processActivities,
    )
  } else if (message.reasoning) {
    merged.processActivities = appendReasoningProcess(merged.processActivities, message.reasoning)
  }

  if (message.toolName) {
    const tool = {
      name: message.toolName,
      callId: message.toolCallId,
      arguments: parseToolArguments(message.metadata?.toolArguments),
    }
    merged.toolActivities = mergeToolActivity(merged.toolActivities, tool)
    if (!hasOrderedProcesses) merged.processActivities = mergeToolProcess(merged.processActivities, tool)
  }
  for (const tool of message.toolActivities || []) {
    merged.toolActivities = mergeToolActivity(merged.toolActivities, tool)
    if (!hasOrderedProcesses) merged.processActivities = mergeToolProcess(merged.processActivities, tool)
    applyPlanTool(merged, tool)
  }
  if (!merged.plan) merged.plan = message.plan
  return merged
}

/**
 * 解析 update_plan 工具结果 / plan_updated 事件载荷为计划快照。
 * 历史行可能来自旧版 PascalCase 序列化（Step/Status），宽容两种键形；
 * 解析失败或空计划返回 undefined，调用方保持原快照不变。
 */
export function parsePlanSnapshot(raw?: string | null): PlanSnapshot | undefined {
  if (!raw) return undefined
  try {
    const parsed = JSON.parse(raw) as {
      plan?: Array<{ step?: unknown, Step?: unknown, status?: unknown, Status?: unknown }>
      completed?: unknown
      total?: unknown
    }
    const steps = (parsed.plan || [])
      .map(item => ({
        step: String(item.step ?? item.Step ?? ''),
        status: item.status ?? item.Status,
      }))
      .filter(item => item.step)
      .map(item => ({ step: item.step, status: normalizePlanStatus(item.status) }))
    if (!steps.length) return undefined
    return {
      plan: steps,
      completed: typeof parsed.completed === 'number' ? parsed.completed : steps.filter(item => item.status === 'completed').length,
      total: typeof parsed.total === 'number' ? parsed.total : steps.length,
    }
  } catch {
    return undefined
  }
}

function normalizePlanStatus(value: unknown): PlanStep['status'] {
  return value === 'completed' || value === 'in_progress' ? value : 'pending'
}

/** update_plan 的工具结果即最新计划快照；其他工具不影响已展示的计划。 */
function applyPlanTool(message: ConversationMessage, tool: ToolActivity): void {
  if (tool.name !== 'update_plan') return
  const snapshot = parsePlanSnapshot(tool.result)
  if (snapshot) message.plan = snapshot
}

function createAssistantMessage(message: ConversationMessage): ConversationMessage {
  return {
    messageId: `assistant-${message.messageId}`,
    sequence: message.sequence,
    role: 'assistant',
    content: '',
    timestamp: message.timestamp,
    toolActivities: [],
    processActivities: [],
  }
}

function mergeToolIntoAssistant(message: ConversationMessage, tool: ToolActivity): void {
  message.toolActivities = mergeToolActivity(message.toolActivities, tool)
  message.processActivities = mergeToolProcess(message.processActivities, tool)
  applyPlanTool(message, tool)
}

/** Append live stream phases without waiting for the persisted conversation snapshot. */
export function appendStreamingReasoning(message: ConversationMessage, content: string): void {
  if (!content) return
  const activities = message.processActivities || []
  const last = activities[activities.length - 1]
  if (last?.kind === 'reasoning') {
    last.content += content
  } else {
    activities.push({ kind: 'reasoning', content })
  }
  message.processActivities = activities
}

/** Keep live tool calls in the same ordered process trace as streamed reasoning. */
export function appendStreamingTool(message: ConversationMessage, tool: ToolActivity): void {
  message.toolActivities = mergeToolActivity(message.toolActivities, tool)
  message.processActivities = mergeToolProcess(message.processActivities, tool)
}

function appendReasoningProcess(
  current: ProcessActivity[] | undefined,
  content: string,
): ProcessActivity[] {
  if (!content) return current || []
  const merged = cloneProcessActivities(current)
  const last = merged[merged.length - 1]
  if (last?.kind === 'reasoning') {
    last.content = appendText(last.content, content)
  } else {
    merged.push({ kind: 'reasoning', content })
  }
  return merged
}

function mergeToolProcess(
  current: ProcessActivity[] | undefined,
  incoming: ToolActivity,
): ProcessActivity[] {
  const merged = cloneProcessActivities(current)
  const tools: ToolActivity[] = []
  const positions: number[] = []
  merged.forEach((item, index) => {
    if (item.kind === 'tool') {
      tools.push(item.tool)
      positions.push(index)
    }
  })
  const matched = matchToolActivity(tools, incoming)
  if (matched < 0) {
    merged.push({ kind: 'tool', tool: { ...incoming } })
    return merged
  }

  const existing = merged[positions[matched]!]
  if (existing?.kind === 'tool') existing.tool = mergeTool(existing.tool, incoming)
  return merged
}

function mergeProcessActivities(
  current: ProcessActivity[] | undefined,
  incoming: ProcessActivity[] | undefined,
): ProcessActivity[] {
  let merged = cloneProcessActivities(current)
  for (const activity of incoming || []) {
    if (activity.kind === 'reasoning') {
      const existingReasoning = merged
        .filter((item): item is Extract<ProcessActivity, { kind: 'reasoning' }> => item.kind === 'reasoning')
        .map(item => item.content)
        .join('\n')
      if (existingReasoning === activity.content || existingReasoning.includes(activity.content)) continue
      const content = existingReasoning && activity.content.startsWith(existingReasoning)
        ? activity.content.slice(existingReasoning.length).trimStart()
        : activity.content
      merged = appendReasoningProcess(merged, content)
    } else {
      merged = mergeToolProcess(merged, activity.tool)
    }
  }
  return merged
}

function cloneProcessActivities(activities?: ProcessActivity[]): ProcessActivity[] {
  return (activities || []).map(activity => activity.kind === 'reasoning'
    ? { ...activity }
    : { kind: 'tool', tool: { ...activity.tool } })
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
  const index = matchToolActivity(merged, incoming)
  if (index < 0) {
    merged.push({ ...incoming })
    return merged
  }

  const existing = merged[index]!
  merged[index] = mergeTool(existing, incoming)
  return merged
}

/**
 * 工具行配对规则：优先按 callId 精确匹配。部分提供方与旧数据不带 callId，
 * 此时"只有结果"的行回填到第一个还没有结果的调用上——否则同一次调用会被
 * 拆成"有参无果"与"有果无参（名为工具）"两条活动。
 */
function matchToolActivity(tools: ToolActivity[], incoming: ToolActivity): number {
  if (incoming.callId) {
    return tools.findIndex(tool => tool.callId === incoming.callId)
  }
  if (incoming.result == null) return -1
  return tools.findIndex(tool => tool.result == null
    && (incoming.name === '工具' || tool.name === '工具' || tool.name === incoming.name))
}

function mergeTool(existing: ToolActivity, incoming: ToolActivity): ToolActivity {
  return {
    name: existing.name === '工具' && incoming.name !== '工具' ? incoming.name : existing.name,
    callId: existing.callId || incoming.callId,
    arguments: incoming.arguments ?? existing.arguments,
    result: incoming.result ?? existing.result,
  }
}

function summaryBoundary(summary: ContextSummary, messages: ConversationMessage[]): number {
  if (summary.sourceEndSequence > 0) return summary.sourceEndSequence
  const compressedAt = Date.parse(summary.lastCompressedAt)
  if (Number.isNaN(compressedAt)) return Number.MAX_SAFE_INTEGER
  return messages.reduce((boundary, message) => {
    const timestamp = Date.parse(message.timestamp)
    return !Number.isNaN(timestamp) && timestamp <= compressedAt
      ? Math.max(boundary, message.sequence)
      : boundary
  }, 0)
}

function appendText(current?: string, incoming?: string): string {
  if (!current) return incoming || ''
  if (!incoming) return current
  // 中断重试会把“部分正文”和“完整正文”各存一行；两行拼接会让片段显示两遍。
  // 一方是另一方子串时保留更完整的一方，不做重复拼接。
  if (current.includes(incoming) || incoming.includes(current)) {
    return current.length >= incoming.length ? current : incoming
  }
  if (current.endsWith('\n') || incoming.startsWith('\n')) return `${current}${incoming}`
  return `${current}\n${incoming}`
}

function preferCompleteText(stored?: string, streamed?: string): string {
  if (!stored) return streamed || ''
  if (!streamed || stored.includes(streamed)) return stored
  if (streamed.includes(stored)) return streamed
  return stored
}

export function parseToolArguments(json?: string | null): unknown {
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

  if (name === 'update_plan') return { kind: '计划', displayName: '更新任务计划' }
  if (name === 'load_skill') return { kind: 'SKILL', displayName: '加载 Skill 指令' }
  if (name === 'read_skill_resource') return { kind: 'SKILL', displayName: '读取 Skill 资源' }
  if (name === 'run_skill_script') return { kind: 'SKILL', displayName: '运行 Skill 脚本' }
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
