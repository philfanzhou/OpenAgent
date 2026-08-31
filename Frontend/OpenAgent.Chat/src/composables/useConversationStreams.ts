import { shallowReactive } from 'vue'
import { randomUuid } from '../browserCrypto'

export type StreamCancelReason = 'user' | 'delete' | 'logout' | 'unload' | 'network'

export interface ConversationStreamState {
  readonly requestId: string
  conversationId: string
  readonly controller: AbortController
  cancelReason?: StreamCancelReason
  readonly settled: Promise<void>
}

interface MutableConversationStreamState extends ConversationStreamState {
  resolveSettled: () => void
}

export function useConversationStreams(
  createRequestId: () => string = () => randomUuid(),
): {
  start: (conversationId: string) => ConversationStreamState
  remap: (requestId: string, conversationId: string) => void
  finish: (requestId: string) => void
  cancelConversation: (conversationId: string, reason: StreamCancelReason) => Promise<void> | undefined
  cancelRequest: (requestId: string, reason: StreamCancelReason) => void
  cancelAll: (reason: StreamCancelReason) => Promise<void>[]
  getByRequest: (requestId: string) => ConversationStreamState | undefined
  isStreaming: (conversationId: string | undefined) => boolean
  activeConversationIds: () => string[]
} {
  const streamsByRequest = shallowReactive(new Map<string, MutableConversationStreamState>())
  const requestsByConversation = shallowReactive(new Map<string, string>())

  function start(conversationId: string): ConversationStreamState {
    if (requestsByConversation.has(conversationId)) {
      throw new Error('This conversation already has an active request')
    }

    const requestId = createRequestId()
    let resolveSettled = (): void => undefined
    const settled = new Promise<void>(resolve => { resolveSettled = resolve })
    const state: MutableConversationStreamState = {
      requestId,
      conversationId,
      controller: new AbortController(),
      settled,
      resolveSettled,
    }
    streamsByRequest.set(requestId, state)
    requestsByConversation.set(conversationId, requestId)
    return state
  }

  function remap(requestId: string, conversationId: string): void {
    const state = streamsByRequest.get(requestId)
    if (!state || state.conversationId === conversationId) return

    const existingRequestId = requestsByConversation.get(conversationId)
    if (existingRequestId && existingRequestId !== requestId) {
      throw new Error('The persisted conversation already has an active request')
    }

    if (requestsByConversation.get(state.conversationId) === requestId) {
      requestsByConversation.delete(state.conversationId)
    }
    state.conversationId = conversationId
    requestsByConversation.set(conversationId, requestId)
  }

  function finish(requestId: string): void {
    const state = streamsByRequest.get(requestId)
    if (!state) return

    streamsByRequest.delete(requestId)
    if (requestsByConversation.get(state.conversationId) === requestId) {
      requestsByConversation.delete(state.conversationId)
    }
    state.resolveSettled()
  }

  function cancelState(state: MutableConversationStreamState, reason: StreamCancelReason): void {
    if (state.controller.signal.aborted) return
    state.cancelReason = reason
    state.controller.abort(reason)
  }

  function cancelConversation(conversationId: string, reason: StreamCancelReason): Promise<void> | undefined {
    const requestId = requestsByConversation.get(conversationId)
    const state = requestId ? streamsByRequest.get(requestId) : undefined
    if (!state) return undefined
    cancelState(state, reason)
    return state.settled
  }

  function cancelRequest(requestId: string, reason: StreamCancelReason): void {
    const state = streamsByRequest.get(requestId)
    if (state) cancelState(state, reason)
  }

  function cancelAll(reason: StreamCancelReason): Promise<void>[] {
    return Array.from(streamsByRequest.values(), state => {
      cancelState(state, reason)
      return state.settled
    })
  }

  function getByRequest(requestId: string): ConversationStreamState | undefined {
    return streamsByRequest.get(requestId)
  }

  function isStreaming(conversationId: string | undefined): boolean {
    return conversationId ? requestsByConversation.has(conversationId) : false
  }

  function activeConversationIds(): string[] {
    return Array.from(requestsByConversation.keys())
  }

  return {
    start,
    remap,
    finish,
    cancelConversation,
    cancelRequest,
    cancelAll,
    getByRequest,
    isStreaming,
    activeConversationIds,
  }
}
