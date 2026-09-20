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

export function interactionSourceLabel(source: number): string {
  if (source === 1) return '压缩摘要'
  return '对话轮次'
}

export function interactionStatusLabel(status: number): string {
  if (status === 1) return '失败'
  if (status === 2) return '已取消'
  return '成功'
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
