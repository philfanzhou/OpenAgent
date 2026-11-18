import { send } from '../../shared/api/http'
import type { StreamEvent } from '../../shared/contracts/types'
import { parseSseBlock } from '../../shared/streaming/parseSseBlock'

async function* readStream(response: Response): AsyncGenerator<StreamEvent> {
  if (!response.body) throw new Error('Engine 未返回流式响应')
  const selectedAgentId = response.headers.get('X-OpenAgent-Selected-Agent-Id')
  if (selectedAgentId) yield { type: 'agent_selected', agentId: selectedAgentId }
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  try {
    while (true) {
      const { done, value } = await reader.read()
      buffer += decoder.decode(value || new Uint8Array(), { stream: !done })
      const blocks = buffer.split(/\r?\n\r?\n/)
      buffer = blocks.pop() || ''
      for (const block of blocks) {
        const event = parseSseBlock(block)
        if (event) yield event
      }
      if (done) break
    }
    const event = parseSseBlock(buffer)
    if (event) yield event
  } finally {
    await reader.cancel().catch(() => {})
    reader.releaseLock()
  }
}

export const api = {
  async *streamChat(
    message: string,
    agentId?: string,
    llmProfileId?: string,
    conversationId?: string,
    fileIds: string[] = [],
    routingConversationId?: string,
    signal?: AbortSignal,
    runtimeId?: string,
  ): AsyncGenerator<StreamEvent> {
    const response = await send('/api/v1/agent/chat/stream', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(routingConversationId ? { 'X-Conversation-Id': routingConversationId } : {}),
      },
      body: JSON.stringify({
        message,
        fileIds,
        context: { ...(runtimeId ? { runtimeId } : {}), ...(agentId ? { agentId } : {}), ...(llmProfileId ? { llmProfileId } : {}), ...(conversationId ? { conversationId } : {}) },
      }),
      signal,
    })
    yield* readStream(response)
  },

  async *resumeChat(conversationId: string, signal?: AbortSignal): AsyncGenerator<StreamEvent> {
    const response = await send(`/api/v1/agent/conversations/${encodeURIComponent(conversationId)}/stream`, { signal })
    // 204 means the producer has already finalized; callers load its persisted result.
    if (response.status !== 204) yield* readStream(response)
  },

  async cancelChat(conversationId: string): Promise<void> {
    await send(`/api/v1/agent/conversations/${encodeURIComponent(conversationId)}/cancel`, { method: 'POST' })
  },
}
