import type { LlmInteractionRecord } from './types'

/** 分页拉取全量交互记录：按 (StartedAt, CallIndex) 升序，短页即止。 */
export async function collectAllInteractions(
  fetchPage: (skip: number, take: number) => Promise<LlmInteractionRecord[]>,
  pageSize = 200,
  maxPages = 50,
): Promise<LlmInteractionRecord[]> {
  const records: LlmInteractionRecord[] = []
  for (let page = 0; page < maxPages; page += 1) {
    const batch = await fetchPage(page * pageSize, pageSize)
    records.push(...batch)
    if (batch.length < pageSize) break
  }
  return records
}

export function interactionStatusLabel(status: number): string {
  if (status === 1) return '失败'
  if (status === 2) return '已取消'
  return '成功'
}

const FILE_TOOLS = new Set(['read_file', 'write_file', 'list_files', 'download_file', 'compress_files', 'publish_files', 'create_file_transfer_url'])
const SKILL_TOOLS = new Set(['load_skill', 'read_skill_resource', 'run_skill_script'])

export interface InteractionCategory {
  /** 主类别：会话 / 工具调用 / 工具结果 / 压缩摘要。 */
  primary: string
  /** 细分标签：代码执行、文件、技能、知识库、用户信息、MCP·<server>、其他、图片。 */
  tags: string[]
  /** 本次调用由模型发起的工具名（来自响应中的 functionCall）。 */
  toolNames: string[]
}

/** 单个工具名到细分标签；MCP 运行时名 mcp__<server>__<tool> 细分到服务器。 */
export function toolCategory(name: string): string {
  if (name.startsWith('mcp__')) {
    const parts = name.split('__')
    return parts.length >= 3 && parts[1] ? `MCP·${parts[1]}` : 'MCP'
  }
  if (name === 'execute_code') return '代码执行'
  if (SKILL_TOOLS.has(name)) return '技能'
  if (FILE_TOOLS.has(name)) return '文件'
  if (name === 'get_current_user_profile') return '用户信息'
  if (name === 'search_knowledge_base') return '知识库'
  return '其他'
}

interface PayloadContent {
  kind?: string
  name?: string | null
}
interface PayloadMessage {
  contents?: PayloadContent[] | null
}

function parsePayload(payload?: string | null): { messages?: PayloadMessage[] | null } | null {
  if (!payload) return null
  try {
    return JSON.parse(payload)
  } catch {
    return null
  }
}

/**
 * 按记录的实际载荷分类：
 * 1. source=Compaction → 压缩摘要；
 * 2. 响应含 functionCall → 工具调用，细分各工具类别（模型本轮要调的工具）；
 * 3. 请求末条消息是 functionResult → 工具结果（带着工具产物再次调用模型的续轮）；
 * 4. 其余 → 会话。请求中出现二进制内容时追加"图片"标签。
 * 载荷被截断或不可解析时降级为按 source 的粗分类，绝不抛错。
 */
export function classifyInteraction(record: Pick<LlmInteractionRecord, 'source' | 'requestJson' | 'responseJson'>): InteractionCategory {
  const toolNames: string[] = []
  if (record.source === 1) return { primary: '压缩摘要', tags: [], toolNames }

  let hasImage = false
  const request = parsePayload(record.requestJson)
  const messages = request?.messages ?? []
  for (const message of messages) {
    if (message?.contents?.some(content => content?.kind === 'data')) hasImage = true
  }

  const response = parsePayload(record.responseJson)
  const responseMessages = response?.messages ?? []
  for (const message of responseMessages) {
    for (const content of message?.contents ?? []) {
      if (content?.kind === 'functionCall' && content.name) toolNames.push(content.name)
    }
  }
  if (toolNames.length > 0) {
    const tags: string[] = []
    for (const name of toolNames) {
      const category = toolCategory(name)
      if (!tags.includes(category)) tags.push(category)
    }
    if (hasImage) tags.push('图片')
    return { primary: '工具调用', tags, toolNames }
  }

  const lastMessage = messages[messages.length - 1]
  if (lastMessage?.contents?.some(content => content?.kind === 'functionResult')) {
    return { primary: '工具结果', tags: hasImage ? ['图片'] : [], toolNames }
  }

  return { primary: '会话', tags: hasImage ? ['图片'] : [], toolNames }
}

export function interactionStatusTagType(status: number): 'success' | 'danger' | 'info' {
  if (status === 1) return 'danger'
  if (status === 2) return 'info'
  return 'success'
}

/** TraceId 通常较长，表格内只显示前 8 位，完整值放 title 提示。 */
export function shortTraceId(traceId: string): string {
  return traceId.length > 8 ? `${traceId.slice(0, 8)}…` : traceId
}

export function formatInteractionTime(startedAt: string): string {
  const date = new Date(startedAt)
  if (Number.isNaN(date.getTime())) return startedAt
  return date.toLocaleTimeString([], { hour12: false })
}

/** 截断过的载荷可能不是合法 JSON，解析失败时原样返回。 */
export function prettyInteractionPayload(payload?: string | null): string {
  if (!payload) return ''
  try {
    return JSON.stringify(JSON.parse(payload), null, 2)
  } catch {
    return payload
  }
}

/** 复制到剪贴板；Clipboard API 缺失或被拒绝（无焦点/无权限）时降级为 execCommand。 */
export async function copyInteractionText(text: string): Promise<void> {
  try {
    const clipboard = globalThis.navigator?.clipboard
    if (clipboard?.writeText) {
      await clipboard.writeText(text)
      return
    }
  } catch {
    // API 被拒绝时继续尝试 execCommand 降级路径。
  }

  const doc = globalThis.document
  if (!doc?.createElement) throw new Error('Clipboard is unavailable')
  const input = doc.createElement('textarea')
  input.value = text
  input.setAttribute('readonly', '')
  input.style.position = 'fixed'
  input.style.opacity = '0'
  doc.body.appendChild(input)
  input.select()
  const copied = doc.execCommand('copy')
  input.remove()
  if (!copied) throw new Error('Clipboard is unavailable')
}
