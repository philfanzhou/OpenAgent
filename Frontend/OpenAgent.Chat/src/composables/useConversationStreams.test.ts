import { computed, ref } from 'vue'
import { describe, expect, it } from 'vitest'
import { useConversationStreams } from './useConversationStreams'

function requestIds(...values: string[]): () => string {
  return () => values.shift() || 'unexpected-request'
}

describe('conversation stream lifecycle', () => {
  it('keeps the original stream active when the selected view changes', () => {
    const streams = useConversationStreams(requestIds('request-a'))
    const selectedConversationId = ref('conversation-a')
    const selectedStreaming = computed(() => streams.isStreaming(selectedConversationId.value))

    streams.start('conversation-a')
    selectedConversationId.value = 'conversation-b'

    expect(streams.isStreaming('conversation-a')).toBe(true)
    expect(selectedStreaming.value).toBe(false)
  })

  it('isolates concurrent controllers and stops only the requested conversation', () => {
    const streams = useConversationStreams(requestIds('request-a', 'request-b'))
    const streamA = streams.start('conversation-a')
    const streamB = streams.start('conversation-b')

    streams.cancelConversation('conversation-a', 'user')

    expect(streamA.controller.signal.aborted).toBe(true)
    expect(streams.getByRequest('request-a')?.cancelReason).toBe('user')
    expect(streamB.controller.signal.aborted).toBe(false)
    expect(streams.isStreaming('conversation-b')).toBe(true)
  })

  it('remaps a temporary conversation id without losing its request identity', () => {
    const streams = useConversationStreams(requestIds('request-a'))
    streams.start('temporary-a')

    streams.remap('request-a', 'persisted-a')

    expect(streams.isStreaming('temporary-a')).toBe(false)
    expect(streams.isStreaming('persisted-a')).toBe(true)
    expect(streams.getByRequest('request-a')?.conversationId).toBe('persisted-a')
  })

  it('finishing one request cannot clear another conversation stream', () => {
    const streams = useConversationStreams(requestIds('request-a', 'request-b'))
    streams.start('conversation-a')
    streams.start('conversation-b')

    streams.finish('request-a')

    expect(streams.isStreaming('conversation-a')).toBe(false)
    expect(streams.isStreaming('conversation-b')).toBe(true)
  })

  it('keeps a reused temporary id isolated from the remapped request', () => {
    const streams = useConversationStreams(requestIds('request-a', 'request-b'))
    streams.start('temporary-a')
    streams.remap('request-a', 'persisted-a')
    streams.start('temporary-a')

    streams.finish('request-a')

    expect(streams.isStreaming('persisted-a')).toBe(false)
    expect(streams.isStreaming('temporary-a')).toBe(true)
    expect(streams.getByRequest('request-b')?.conversationId).toBe('temporary-a')
  })

  it('rejects a second active request for the same conversation', () => {
    const streams = useConversationStreams(requestIds('request-a', 'request-b'))
    streams.start('conversation-a')

    expect(() => streams.start('conversation-a')).toThrow('already has an active request')
  })

  it('cancels every live request on logout or page unload', () => {
    const streams = useConversationStreams(requestIds('request-a', 'request-b'))
    const streamA = streams.start('conversation-a')
    const streamB = streams.start('conversation-b')

    streams.cancelAll('unload')

    expect(streamA.controller.signal.aborted).toBe(true)
    expect(streamB.controller.signal.aborted).toBe(true)
    expect(streams.getByRequest('request-a')?.cancelReason).toBe('unload')
    expect(streams.getByRequest('request-b')?.cancelReason).toBe('unload')
  })

  it('settles deletion waits only after the matching request finishes', async () => {
    const streams = useConversationStreams(requestIds('request-a'))
    streams.start('conversation-a')
    const settled = streams.cancelConversation('conversation-a', 'delete')
    let completed = false
    void settled?.then(() => { completed = true })

    await Promise.resolve()
    expect(completed).toBe(false)

    streams.finish('request-a')
    await settled
    expect(completed).toBe(true)
  })
})
