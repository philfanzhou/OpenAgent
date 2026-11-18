import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { api } from '../api'
import type { ConversationRecord } from '../types'
import { useConversationState } from './useConversationState'
import { useConversationStreams } from './useConversationStreams'

vi.mock('../api', () => ({ api: { getConversation: vi.fn() } }))

const storageKey = 'openagent.chat.selected-conversation-id'

function record(status: ConversationRecord['status']): ConversationRecord {
  return {
    conversationId: 'conversation-1', tenantId: 'tenant-1', userId: 'user-1', agentId: 'agent-1',
    type: 0, status, version: 1, isDeletedByUser: false,
    createdAt: '2026-11-18T00:00:00Z', updatedAt: '2026-11-18T00:00:00Z', lastMessageAt: '2026-11-18T00:00:00Z',
    messageCount: 1, messages: [{ messageId: 'input-1', sequence: 1, role: 'user', content: '继续生成', timestamp: '2026-11-18T00:00:00Z' }],
  }
}

function setup() {
  const loaded = vi.fn()
  const notifyError = vi.fn()
  const streams = useConversationStreams()
  const state = useConversationState({
    selectedAgentId: ref(''), selectedLlmProfileId: ref(''), streams,
    hydrateFilePreviews: async () => {}, notifyError, onSelectedConversationDeleted: vi.fn(), onConversationLoaded: loaded,
  })
  return { state, loaded, notifyError, streams }
}

beforeEach(() => {
  vi.resetAllMocks()
  const values = new Map<string, string>()
  vi.stubGlobal('sessionStorage', {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => values.set(key, value),
    removeItem: (key: string) => values.delete(key),
  })
})

describe('conversation restoration', () => {
  it('restores an accepted turn from its stored ID before it appears in the persisted list', async () => {
    const { state, loaded } = setup()
    sessionStorage.setItem(storageKey, 'conversation-1')
    vi.mocked(api.getConversation).mockResolvedValue(record('Running'))

    await state.restoreSelectedConversation()

    expect(api.getConversation).toHaveBeenCalledWith('conversation-1')
    expect(state.selectedConversation.value?.messages?.[0]?.content).toBe('继续生成')
    expect(loaded).toHaveBeenCalledWith(expect.objectContaining({ status: 'Running' }))
  })

  it('reloads a disconnected running conversation even when its cached messages are nonempty', async () => {
    const { state, loaded } = setup()
    const cached = record('Running')
    const completed = record('Completed')
    completed.messages!.push({ messageId: 'result-1', sequence: 2, role: 'assistant', content: '生成完成', timestamp: '2026-11-18T00:01:00Z' })
    vi.mocked(api.getConversation).mockResolvedValue(completed)

    await state.selectConversation(cached)

    expect(state.selectedConversation.value?.status).toBe('Completed')
    expect(state.currentMessages.value.at(-1)?.content).toBe('生成完成')
    expect(loaded).toHaveBeenCalledWith(completed)
  })

  it('does not replace an attached stream with a detail snapshot when switching back to it', async () => {
    const { state, streams } = setup()
    const attached = record('Running')
    const stream = streams.start(attached.conversationId)

    await state.selectConversation(attached)

    expect(api.getConversation).not.toHaveBeenCalled()
    expect(state.selectedConversationStreaming.value).toBe(true)
    streams.finish(stream.requestId)
  })
})
