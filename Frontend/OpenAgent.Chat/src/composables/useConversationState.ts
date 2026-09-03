import { computed, ref, shallowReactive, type Ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api'
import { randomUuid } from '../browserCrypto'
import { mergeConversationRecords, replaceConversationRecord, selectionMatchesConversation } from '../conversationCollection'
import { summarizeConversationUsage } from '../tokenUsage'
import type { ConversationRecord } from '../types'
import type { useConversationStreams } from './useConversationStreams'

const selectedConversationStorageKey = 'openagent.chat.selected-conversation-id'

interface ConversationStateOptions {
  selectedAgentId: Ref<string>
  selectedLlmProfileId: Ref<string>
  streams: ReturnType<typeof useConversationStreams>
  hydrateFilePreviews: (conversation: ConversationRecord) => Promise<void>
  notifyError: (error: unknown) => void
  onSelectedConversationDeleted: () => void
}

export function useConversationState(options: ConversationStateOptions) {
  const conversations = ref<ConversationRecord[]>([])
  const selectedConversation = ref<ConversationRecord | null>(null)
  const conversationDetailRequests = shallowReactive(new Map<string, string>())
  const compactingConversation = ref(false)

  const currentMessages = computed(() => selectedConversation.value?.messages || [])
  const streamingConversationIds = computed(() => options.streams.activeConversationIds())
  const loadingConversation = computed(() => selectedConversation.value
    ? conversationDetailRequests.has(selectedConversation.value.conversationId)
    : false)
  const selectedConversationStreaming = computed(() => options.streams.isStreaming(selectedConversation.value?.conversationId))
  const conversationStatusText = computed(() => {
    if (!selectedConversation.value) return '新建'
    if (selectedConversation.value.status === 'Running' && !selectedConversationStreaming.value) {
      return 'Running（当前页面未连接流）'
    }
    return selectedConversation.value.status
  })
  const currentUsageSummary = computed(() => summarizeConversationUsage(currentMessages.value))

  function mergeConversationList(refreshed: ConversationRecord[]): void {
    conversations.value = mergeConversationRecords(
      conversations.value,
      refreshed,
      new Set(options.streams.activeConversationIds()),
      selectedConversation.value?.conversationId,
    )
  }

  function replaceConversation(detail: ConversationRecord, previousConversationId = detail.conversationId): void {
    const selectionMatches = selectionMatchesConversation(
      selectedConversation.value?.conversationId,
      detail.conversationId,
      previousConversationId,
    )
    conversations.value = replaceConversationRecord(conversations.value, detail, previousConversationId)
    if (selectionMatches) {
      selectedConversation.value = detail
      sessionStorage.setItem(selectedConversationStorageKey, detail.conversationId)
    }
  }

  async function refreshConversations(showError = true): Promise<void> {
    try {
      mergeConversationList(await api.listConversations())
    } catch (error) {
      if (showError) options.notifyError(error)
    }
  }

  async function restoreSelectedConversation(): Promise<void> {
    if (selectedConversation.value) return
    const storedConversationId = sessionStorage.getItem(selectedConversationStorageKey)
    const stored = conversations.value.find(item => item.conversationId === storedConversationId)
    if (stored) await selectConversation(stored)
  }

  async function selectConversation(item: ConversationRecord): Promise<void> {
    selectedConversation.value = item
    sessionStorage.setItem(selectedConversationStorageKey, item.conversationId)
    options.selectedAgentId.value = item.agentId || options.selectedAgentId.value
    if (item.messages?.length) {
      await options.hydrateFilePreviews(item)
      return
    }
    const requestId = randomUuid()
    conversationDetailRequests.set(item.conversationId, requestId)
    try {
      const detail = await api.getConversation(item.conversationId)
      await options.hydrateFilePreviews(detail)
      if (conversationDetailRequests.get(item.conversationId) !== requestId
        || options.streams.isStreaming(item.conversationId)) return
      replaceConversation(detail, item.conversationId)
    } catch (error) {
      if (conversationDetailRequests.get(item.conversationId) === requestId
        && selectedConversation.value?.conversationId === item.conversationId) options.notifyError(error)
    } finally {
      if (conversationDetailRequests.get(item.conversationId) === requestId) {
        conversationDetailRequests.delete(item.conversationId)
      }
    }
  }

  function clearSelectedConversation(): void {
    selectedConversation.value = null
    sessionStorage.removeItem(selectedConversationStorageKey)
  }

  async function deleteConversation(item: ConversationRecord): Promise<void> {
    try {
      await ElMessageBox.confirm('确认删除这个会话吗？', '删除会话', { type: 'warning' })
      conversationDetailRequests.delete(item.conversationId)
      const settled = options.streams.cancelConversation(item.conversationId, 'delete')
      if (settled) await settled
      await api.deleteConversation(item.conversationId)
      conversations.value = conversations.value.filter(value => value.conversationId !== item.conversationId)
      if (selectedConversation.value?.conversationId === item.conversationId) options.onSelectedConversationDeleted()
    } catch (error) {
      if (error !== 'cancel' && error !== 'close') options.notifyError(error)
    }
  }

  async function compactConversation(): Promise<void> {
    const conversation = selectedConversation.value
    if (!conversation || !options.selectedLlmProfileId.value || selectedConversationStreaming.value) return
    compactingConversation.value = true
    try {
      const summary = await api.compactConversation(conversation.conversationId, options.selectedLlmProfileId.value)
      conversation.contextSummaries = [...(conversation.contextSummaries || []), summary]
      if (summary.status === 'Succeeded') ElMessage.success('会话上下文压缩已完成')
      else if (summary.status === 'Skipped') ElMessage.info('本次压缩未执行，原始会话保持不变')
      else ElMessage.warning('压缩失败，原始会话历史已恢复')
    } catch (error) {
      options.notifyError(error)
      try {
        const persisted = await api.getConversation(conversation.conversationId)
        replaceConversation(persisted, conversation.conversationId)
      } catch {
        // The original error remains the actionable result.
      }
    } finally {
      compactingConversation.value = false
    }
  }

  function resetConversations(): void {
    conversations.value = []
    selectedConversation.value = null
    conversationDetailRequests.clear()
  }

  return {
    conversations,
    selectedConversation,
    conversationDetailRequests,
    currentMessages,
    streamingConversationIds,
    loadingConversation,
    selectedConversationStreaming,
    compactingConversation,
    conversationStatusText,
    currentUsageSummary,
    mergeConversationList,
    replaceConversation,
    refreshConversations,
    restoreSelectedConversation,
    selectConversation,
    clearSelectedConversation,
    deleteConversation,
    compactConversation,
    resetConversations,
  }
}
