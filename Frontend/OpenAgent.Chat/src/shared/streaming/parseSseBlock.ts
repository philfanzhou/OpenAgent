import type { StreamEvent } from '../contracts/types'

export function parseSseBlock(block: string): StreamEvent | null {
  let eventType = 'message'
  const data: string[] = []
  for (const line of block.split(/\r?\n/)) {
    if (line.startsWith(':')) continue
    if (line.startsWith('event:')) eventType = line.slice(6).trim()
    else if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
  }
  if (!data.length) return null

  const payload = JSON.parse(data.join('\n')) as Record<string, unknown>
  if (eventType === 'error' && typeof payload.detail === 'string') {
    return { type: 'error', error: payload as StreamEvent['error'] }
  }
  return { ...payload, type: eventType } as StreamEvent
}
