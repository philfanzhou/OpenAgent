import { ref, type ComputedRef, type Ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api, makeLocalConversation } from '../api'
import { mergeAssistantSnapshot } from '../messagePresentation'
import { createStreamingAssistantContentState, enqueueAssistantContent, markAssistantPhaseBoundary } from '../streamingAssistantContent'
import { createTypewriterQueue, type TypewriterQueue } from '../typewriterQueue'
import { AUTO_AGENT_ID, type AgentSummary, type ConversationMessage, type ConversationRecord, type PendingFile } from '../types'
import { toMessageFile } from './useFileHandling'
import type { useConversationStreams } from './useConversationStreams'

const selectedConversationStorageKey = 'openagent.chat.selected-conversation-id'

interface ChatStreamingOptions {
  selectedAgentId: Ref<string>
  agents: Ref<AgentSummary[]>
  conversations: Ref<ConversationRecord[]>
  selectedConversation: Ref<ConversationRecord | null>
  selectedConversationStreaming: ComputedRef<boolean>
  pendingFiles: Ref<PendingFile[]>
  streams: ReturnType<typeof useConversationStreams>
  hydrateFilePreviews: (conversation: ConversationRecord) => Promise<void>
  replaceConversation: (detail: ConversationRecord, previousConversationId?: string) => void
  refreshConversations: (showError?: boolean) => Promise<void>
  notifyError: (error: unknown) => void
}

export function useChatStreaming(options: ChatStreamingOptions) {
  const message = ref('')

  function stopStreaming(): void {
    const conversationId = options.selectedConversation.value?.conversationId
    if (conversationId) options.streams.cancelConversation(conversationId, 'user')
  }

  function clearDraft(): void {
    message.value = ''
    options.pendingFiles.value = []
  }

  async function send(): Promise<void> {
    const content = message.value.trim()
    const hasFiles = options.pendingFiles.value.length > 0
    if ((!content && !hasFiles) || !options.selectedAgentId.value || options.selectedConversationStreaming.value) return
    if (options.pendingFiles.value.some(item => item.state !== 'ready' || !item.asset)) {
      options.notifyError(new Error('请等待文件上传完成，或移除上传失败的文件后再发送'))
      return
    }
    const requestContent = content || '请处理我上传的文件'
    const isNewConversation = !options.selectedConversation.value
    const uploaded = options.pendingFiles.value
      .map(item => item.asset)
      .filter((asset): asset is NonNullable<typeof asset> => asset != null)
    const requestedAgentId = options.selectedAgentId.value === AUTO_AGENT_ID
      ? options.selectedConversation.value?.agentId
      : options.selectedAgentId.value
    const local = options.selectedConversation.value || makeLocalConversation(requestedAgentId || '', requestContent)
    if (isNewConversation) {
      local.messages = []
      local.messageCount = 0
      options.selectedConversation.value = local
      options.conversations.value = [local, ...options.conversations.value.filter(item => item.conversationId !== local.conversationId)]
    }
    const conversation = options.selectedConversation.value
    if (!conversation) return
    let conversationId = conversation.conversationId
    // Send the local id even on the first message. Uploaded assets were created
    // for this conversation; omitting it made Engine generate a different id and
    // caused the first message's file references to miss their scope.
    const sendConversationId = conversationId
    const streamState = options.streams.start(conversationId)
    const requestId = streamState.requestId
    let streamError: { title?: string; detail?: string; traceId?: string } | undefined
    let flushStream: (() => void) | undefined
    let contentQueue: TypewriterQueue | undefined
    let streamedAssistant: ConversationMessage | undefined
    let receivedDone = false
    let completedAgentId = conversation.agentId
    const assistantContentState = createStreamingAssistantContentState()
    let showedEarlyRoutingNotice = false
    try {
      conversation.messages ||= []
      conversation.status = 'Running'
      const messageFiles = await Promise.all(options.pendingFiles.value.map(toMessageFile))
      conversation.messages.push({
        messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
        role: 'user', content: content || '已上传文件', timestamp: new Date().toISOString(),
        files: messageFiles,
      })
      conversation.messageCount = conversation.messages.length
      conversation.updatedAt = new Date().toISOString()
      conversation.lastMessageAt = conversation.updatedAt
      message.value = ''
      options.pendingFiles.value = []
      let reasoning = ''
      let lastFlush = 0
      conversation.messages.push({
        messageId: crypto.randomUUID(), sequence: conversation.messages.length + 1,
        role: 'assistant', content: '', timestamp: new Date().toISOString(),
      })
      const assistantMessage = conversation.messages[conversation.messages.length - 1]!
      streamedAssistant = assistantMessage
      conversation.messageCount = conversation.messages.length
      contentQueue = createTypewriterQueue(content => {
        assistantMessage.content += content
      })
      flushStream = (): void => {
        contentQueue?.flush()
        assistantMessage.reasoning = reasoning || undefined
        lastFlush = performance.now()
      }
      // Keep content paced by the typewriter queue and throttle reasoning renders.
      for await (const event of api.streamChat(
        requestContent,
        requestedAgentId,
        sendConversationId,
        uploaded.map(asset => asset.fileId),
        sendConversationId,
        streamState.controller.signal,
      )) {
        if (event.conversationId && event.conversationId !== conversationId) {
          const previousConversationId = conversationId
          const selectionMatches = options.selectedConversation.value === conversation
            || options.selectedConversation.value?.conversationId === previousConversationId
          options.streams.remap(requestId, event.conversationId)
          conversationId = event.conversationId
          conversation.conversationId = conversationId
          if (selectionMatches) sessionStorage.setItem(selectedConversationStorageKey, conversationId)
        }
        if (event.type === 'agent_selected') {
          if (isNewConversation && options.selectedAgentId.value === AUTO_AGENT_ID && event.agentId) {
            completedAgentId = event.agentId
            conversation.agentId = event.agentId
            options.selectedAgentId.value = event.agentId
            const routed = options.agents.value.find(agent => agent.agentId === event.agentId)
            ElMessage.info(`已由意图识别路由到 Agent「${routed?.name || event.agentId}」`)
            showedEarlyRoutingNotice = true
          }
        } else if (event.type === 'content') {
          enqueueAssistantContent(assistantContentState, content => contentQueue?.enqueue(content), event.content || '')
        } else if (event.type === 'reasoning') {
          reasoning += event.content || ''
          if (performance.now() - lastFlush > 100) {
            assistantMessage.reasoning = reasoning
            lastFlush = performance.now()
          }
        } else if (event.type === 'tool_call') {
          markAssistantPhaseBoundary(assistantContentState)
          assistantMessage.toolActivities ||= []
          assistantMessage.toolActivities.push({
            name: event.toolName || '工具',
            callId: event.toolCallId,
            arguments: event.toolArguments,
          })
        } else if (event.type === 'done') {
          flushStream?.()
          receivedDone = true
          conversation.status = (event.status || 'Completed') as ConversationRecord['status']
          assistantMessage.tokenUsage = event.usage ?? undefined
          assistantMessage.modelId = event.modelId ?? undefined
        } else if (event.type === 'error') {
          flushStream?.()
          streamError = {
            title: event.error?.title,
            detail: event.error?.detail || 'Agent 执行失败',
            traceId: event.error?.traceId,
          }
          throw new Error(streamError.detail)
        }
      }
      flushStream?.()
      if (!receivedDone) throw new Error('流连接在完成事件前意外结束')
      try {
        const persisted = await api.getConversation(conversationId)
        await options.hydrateFilePreviews(persisted)
        persisted.messages = mergeAssistantSnapshot(persisted.messages || [], assistantMessage)
        completedAgentId = persisted.agentId
        options.replaceConversation(persisted, conversationId)
      } catch (error) {
        if (options.selectedConversation.value?.conversationId === conversationId) options.notifyError(error)
      }
      await options.refreshConversations(false)
      if (!showedEarlyRoutingNotice && isNewConversation && completedAgentId && completedAgentId !== requestedAgentId
        && options.selectedConversation.value?.conversationId === conversationId) {
        const routed = options.agents.value.find(agent => agent.agentId === completedAgentId)
        options.selectedAgentId.value = completedAgentId
        ElMessage.info(`已由意图识别路由到 Agent「${routed?.name || completedAgentId}」`)
      }
    } catch (error) {
      flushStream?.()
      const cancelReason = options.streams.getByRequest(requestId)?.cancelReason
      if (cancelReason && cancelReason !== 'network') {
        conversation.status = 'Cancelled'
        conversation.updatedAt = new Date().toISOString()
        conversation.lastMessageAt = conversation.updatedAt
        if (cancelReason !== 'unload') {
          try {
            const persisted = await api.getConversation(conversationId)
            await options.hydrateFilePreviews(persisted)
            if (streamedAssistant) {
              persisted.messages = mergeAssistantSnapshot(persisted.messages || [], streamedAssistant)
            }
            if (persisted.messages?.length && persisted.status !== 'Running') {
              options.replaceConversation(persisted, conversationId)
            }
          } catch {
            // The server may not have persisted a newly cancelled conversation yet.
          }
          await options.refreshConversations(false)
        }
      } else {
        if (!streamError) options.streams.cancelRequest(requestId, 'network')
        conversation.status = 'Failed'
        const lastMessage = conversation.messages?.at(-1)
        if (lastMessage?.role === 'assistant') {
          lastMessage.error = {
            title: streamError?.title || 'Agent 执行失败',
            detail: streamError?.detail || (error instanceof Error ? error.message : '执行失败'),
            traceId: streamError?.traceId,
          }
        }
        try {
          const persisted = await api.getConversation(conversationId)
          await options.hydrateFilePreviews(persisted)
          if (streamedAssistant) {
            persisted.messages = mergeAssistantSnapshot(persisted.messages || [], streamedAssistant)
          }
          if (persisted.messages?.length && persisted.status !== 'Running') {
            options.replaceConversation(persisted, conversationId)
          }
        } catch {
          // Reconnect and conversation detail loading will recover persisted messages later.
        }
        if (options.selectedConversation.value?.conversationId === conversationId) options.notifyError(error)
      }
    } finally {
      contentQueue?.clear()
      options.streams.finish(requestId)
    }
  }

  return { message, send, stopStreaming, clearDraft }
}
