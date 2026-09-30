import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { api } from '../api'
import { useConversationState } from './useConversationState'
import { useConversationStreams } from './useConversationStreams'
import type { ContextSummary, ConversationRecord } from '../types'

vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} })
vi.mock('../api', () => ({ api: { compactConversation: vi.fn(), getConversation: vi.fn() } }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn(), info: vi.fn(), warning: vi.fn() }, ElMessageBox: { confirm: vi.fn() } }))

const summary: ContextSummary = {
  compressionId: 'compact-1', strategy: 'summarization', trigger: 'Manual', status: 'Succeeded',
  summary: 'task state', lastCompressedAt: '2026-09-30T00:00:00Z', compressedMessageCount: 4,
  originalStartSequence: 1, originalEndSequence: 4, originalTokenCount: 1000, tokenCount: 300,
  originalHistoryRestored: false, sourceEndSequence: 6,
}

function record(id: string, contextSummaries: ContextSummary[] = []): ConversationRecord {
  return { conversationId: id, agentId: 'agent', status: 'Completed', messages: [], contextSummaries } as unknown as ConversationRecord
}

function setup() {
  const notifyError = vi.fn()
  const state = useConversationState({ selectedAgentId: ref('agent'), selectedLlmProfileId: ref('profile'),
    streams: useConversationStreams(), hydrateFilePreviews: async () => {}, notifyError, onSelectedConversationDeleted: () => {} })
  state.conversations.value = [record('one'), record('two')]
  state.selectedConversation.value = state.conversations.value[0]!
  return { state, notifyError }
}

describe('manual compaction state', () => {
  beforeEach(() => { vi.clearAllMocks() })

  it('blocks duplicate requests and attaches the result to its original conversation after selection changes', async () => {
    const { state } = setup()
    let complete!: (summary: ContextSummary) => void
    vi.mocked(api.compactConversation).mockReturnValue(new Promise(resolve => { complete = resolve }))
    vi.mocked(api.getConversation).mockResolvedValue(record('one', [summary]))
    const request = state.compactConversation()
    await state.compactConversation()
    expect(api.compactConversation).toHaveBeenCalledTimes(1)
    expect(state.compactingConversation.value).toBe(true)
    state.selectedConversation.value = state.conversations.value[1]!
    expect(state.compactingConversation.value).toBe(false)
    expect(state.isCompactingConversation('one')).toBe(true)
    complete(summary)
    await request
    expect(state.selectedConversation.value?.conversationId).toBe('two')
    expect(state.conversations.value[0]?.contextSummaries).toEqual([summary])
    expect(state.isCompactingConversation('one')).toBe(false)
  })

  it('keeps a returned audit if refreshing the detail fails', async () => {
    const { state, notifyError } = setup()
    vi.mocked(api.compactConversation).mockResolvedValue(summary)
    vi.mocked(api.getConversation).mockRejectedValue(new Error('refresh disconnected'))
    await state.compactConversation()
    expect(state.selectedConversation.value?.contextSummaries).toEqual([summary])
    expect(notifyError).toHaveBeenCalledTimes(1)
    expect(state.compactingConversation.value).toBe(false)
  })

  it('reloads persisted failures when the POST response is lost', async () => {
    const { state, notifyError } = setup()
    const failure = { ...summary, status: 'Failed', error: 'provider unavailable', originalHistoryRestored: true }
    vi.mocked(api.compactConversation).mockRejectedValue(new Error('connection lost'))
    vi.mocked(api.getConversation).mockResolvedValue(record('one', [failure]))
    await state.compactConversation()
    expect(state.selectedConversation.value?.contextSummaries).toEqual([failure])
    expect(notifyError).toHaveBeenCalledTimes(1)
    expect(state.compactingConversation.value).toBe(false)
  })
})
