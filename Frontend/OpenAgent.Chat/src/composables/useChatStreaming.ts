import { computed, ref, type ComputedRef, type Ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api, makeLocalConversation } from '../api'
import { randomUuid } from '../browserCrypto'
import { mergeOptimisticUserMessages } from '../conversationCollection'
import { describeQueueFull, describeQueueTimeout } from '../features/chat/queueNotice'
import { appendStreamingReasoning, appendStreamingTool, mergeAssistantSnapshot, parsePlanSnapshot } from '../messagePresentation'
import { createStreamingAssistantContentState, enqueueAssistantContent, markAssistantPhaseBoundary } from '../streamingAssistantContent'
import { createTypewriterQueue, type TypewriterQueue } from '../typewriterQueue'
import { AUTO_AGENT_ID, type AgentSummary, type ConversationMessage, type ConversationRecord, type PendingFile } from '../types'
import { toMessageFile } from './useFileHandling'
import type { useConversationStreams } from './useConversationStreams'

const selectedConversationStorageKey = 'openagent.chat.selected-conversation-id'

interface ChatStreamingOptions {
  selectedAgentId: Ref<string>
  selectedLlmProfileId: Ref<string>
  selectedRuntimeId?: Ref<string>
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

function lastUserMessage(messages: ConversationMessage[] = []): ConversationMessage | undefined {
  for (let index = messages.length - 1; index >= 0; index--) {
    if (messages[index]?.role === 'user') return messages[index]
  }
  return undefined
}

export function useChatStreaming(options: ChatStreamingOptions) {
  const message = ref('')

  const canRetry = computed(() => {
    const conversation = options.selectedConversation.value
    const last = conversation?.messages?.at(-1)
    return Boolean(conversation && !options.streams.isStreaming(conversation.conversationId)
      && (conversation.status === 'Failed' || last?.error || last?.metadata?.executionStatus === 'Failed')
      && conversation.messages?.some(item => item.role === 'user'))
  })

  async function stopStreaming(): Promise<void> {
    const conversationId = options.selectedConversation.value?.conversationId
    if (!conversationId) return
    const stream = options.streams.getByConversation(conversationId)
    if (stream) stream.cancelReason = 'user'
    try {
      await api.cancelChat(conversationId)
      options.streams.cancelConversation(conversationId, 'user')
    } catch (error) {
      if (stream && !stream.controller.signal.aborted) stream.cancelReason = undefined
      options.notifyError(error)
    }
  }

  function clearDraft(): void {
    message.value = ''
    options.pendingFiles.value = []
  }

  async function runTurn(resume?: ConversationRecord, retryMessage?: ConversationMessage): Promise<void> {
    if (resume) {
      const selected = options.selectedConversation.value
      if (selected?.conversationId === resume.conversationId) resume = selected
    }
    if (!resume && !options.selectedLlmProfileId.value) {
      options.notifyError(new Error('请先保存并选择模型连接'))
      return
    }
    const content = retryMessage?.content ?? message.value.trim()
    const hasFiles = (retryMessage?.files?.length ?? options.pendingFiles.value.length) > 0
    if (!resume && ((!content && !hasFiles) || !options.selectedAgentId.value || options.selectedConversationStreaming.value)) return
    if (options.streams.isStreaming((resume ?? options.selectedConversation.value)?.conversationId)) return
    if (!resume && !retryMessage && options.pendingFiles.value.some(item => item.state !== 'ready' || !item.asset)) {
      options.notifyError(new Error('请等待文件上传完成，或移除上传失败的文件后再发送'))
      return
    }
    const requestContent = content || '请处理我上传的文件'
    const isNewConversation = !options.selectedConversation.value
    const uploaded = options.pendingFiles.value
      .map(item => item.asset)
      .filter((asset): asset is NonNullable<typeof asset> => asset != null)
    const requestedAgentId = resume?.agentId || (retryMessage ? options.selectedConversation.value?.agentId : undefined)
      || (options.selectedAgentId.value === AUTO_AGENT_ID
        ? options.selectedConversation.value?.agentId
        : options.selectedAgentId.value)
    const local = resume || options.selectedConversation.value || makeLocalConversation(requestedAgentId || '', requestContent)
    if (isNewConversation) {
      local.messages = []
      local.messageCount = 0
      options.selectedConversation.value = local
      options.conversations.value = [local, ...options.conversations.value.filter(item => item.conversationId !== local.conversationId)]
    }
    const conversation = resume || options.selectedConversation.value
    if (!conversation) return
    let conversationId = conversation.conversationId
    if (options.selectedConversation.value?.conversationId === conversationId) {
      sessionStorage.setItem(selectedConversationStorageKey, conversationId)
    }
    // Send the local id even on the first message. Uploaded assets were created
    // for this conversation; omitting it made Engine generate a different id and
    // caused the first message's file references to miss their scope.
    const sendConversationId = conversationId
    const streamState = options.streams.start(conversationId)
    const requestId = streamState.requestId
    let streamError: { title?: string; detail?: string; traceId?: string; code?: string } | undefined
    let flushStream: (() => void) | undefined
    let contentQueue: TypewriterQueue | undefined
    let streamedAssistant: ConversationMessage | undefined
    let receivedDone = false
    let completedAgentId = conversation.agentId
    let receivedEvent = false
    const assistantContentState = createStreamingAssistantContentState()
    let showedEarlyRoutingNotice = false
    try {
      conversation.messages ||= []
      conversation.status = 'Running'
      // 会话内存的是合并后的展示消息（tool 行被折叠），行数与真实 Sequence 有落差，
      // 不能用数组长度推算新序号；取现有最大序号递增，保证乐观消息始终排在最后。
      const baseSequence = conversation.messages.reduce(
        (max, item) => Math.max(max, item.sequence || 0), 0)
      if (resume) {
        // The endpoint replays the entire active turn; do not append it to an old partial response.
        const input = lastUserMessage(conversation.messages)
        conversation.messages = input ? conversation.messages.slice(0, conversation.messages.indexOf(input) + 1) : []
      } else {
        const messageFiles = retryMessage?.files ?? await Promise.all(options.pendingFiles.value.map(toMessageFile))
        conversation.messages.push({
          messageId: randomUuid(), sequence: baseSequence + 1,
          role: 'user', content: requestContent, timestamp: new Date().toISOString(),
          files: messageFiles,
        })
        conversation.updatedAt = new Date().toISOString()
        conversation.lastMessageAt = conversation.updatedAt
        if (!retryMessage) {
          message.value = ''
          options.pendingFiles.value = []
        }
      }
      conversation.messageCount = conversation.messages.length
      let reasoning = ''
      let lastFlush = 0
      conversation.messages.push({
        messageId: randomUuid(), sequence: resume ? (conversation.messages.at(-1)?.sequence || 0) + 1 : baseSequence + 2,
        role: 'assistant', content: '', timestamp: new Date().toISOString(),
      })
      const assistantMessage = conversation.messages[conversation.messages.length - 1]!
      streamedAssistant = assistantMessage
      conversation.messageCount = conversation.messages.length
      contentQueue = createTypewriterQueue(content => {
        assistantMessage.content += content
      }, { intervalMs: 48 })
      flushStream = (): void => {
        contentQueue?.flush()
        assistantMessage.reasoning = reasoning || undefined
        lastFlush = performance.now()
      }
      // Keep content paced by the typewriter queue and throttle reasoning renders.
      const events = resume
        ? api.resumeChat(conversationId, streamState.controller.signal)
        : api.streamChat(
            requestContent,
            requestedAgentId,
            options.selectedLlmProfileId.value,
            sendConversationId,
            retryMessage ? (retryMessage.files || retryMessage.metadata?.files || []).map(file => file.fileId).filter((id): id is string => Boolean(id)) : uploaded.map(asset => asset.fileId),
            sendConversationId,
            streamState.controller.signal,
            options.selectedRuntimeId?.value,
          )
      for await (const event of events) {
        receivedEvent = true
        if (event.conversationId && event.conversationId !== conversationId) {
          const previousConversationId = conversationId
          const selectionMatches = options.selectedConversation.value === conversation
            || options.selectedConversation.value?.conversationId === previousConversationId
          // 服务端分配的会话 ID 已被另一个活跃流占用（重复打开同一持久会话）时，
          // 不做本地重映射，也不把这种冲突当网络失败处理（避免误标 Failed）。
          let remapConflict = false
          try {
            options.streams.remap(requestId, event.conversationId)
          } catch {
            remapConflict = true
          }
          if (remapConflict) {
            ElMessage.warning('该会话已在另一个窗口处理中，本窗口结果不再同步')
          } else {
            conversationId = event.conversationId
            conversation.conversationId = conversationId
            if (selectionMatches) sessionStorage.setItem(selectedConversationStorageKey, conversationId)
          }
        }
        if (event.type === 'agent_selected') {
          // 仅当本流仍对应当前选中会话时才回填全局选择器，避免切换会话/新对话后被迟到的路由结果串台。
          const selectionMatches = options.selectedConversation.value === conversation
            || options.selectedConversation.value?.conversationId === conversationId
          if (isNewConversation && selectionMatches && options.selectedAgentId.value === AUTO_AGENT_ID && event.agentId) {
            completedAgentId = event.agentId
            conversation.agentId = event.agentId
            options.selectedAgentId.value = event.agentId
            const routed = options.agents.value.find(agent => agent.agentId === event.agentId)
            ElMessage.info(`已由意图识别路由到 Agent「${routed?.name || event.agentId}」`)
            showedEarlyRoutingNotice = true
          }
        } else if (event.type === 'execution_status') {
          if (event.status === 'queued' || event.status === 'started') assistantMessage.executionStatus = event.status
        } else if (event.type === 'content') {
          enqueueAssistantContent(assistantContentState, content => contentQueue?.enqueue(content), event.content || '')
        } else if (event.type === 'reasoning') {
          const reasoningContent = event.content || ''
          reasoning += reasoningContent
          appendStreamingReasoning(assistantMessage, reasoningContent)
          if (performance.now() - lastFlush > 100) {
            assistantMessage.reasoning = reasoning
            lastFlush = performance.now()
          }
        } else if (event.type === 'tool_call') {
          markAssistantPhaseBoundary(assistantContentState)
          appendStreamingTool(assistantMessage, {
            name: event.toolName || '工具',
            callId: event.toolCallId,
            arguments: event.toolArguments,
          })
        } else if (event.type === 'tool_result') {
          // 工具结果随流即时回填到对应调用行，无需等整轮结束重载历史。
          const tool = assistantMessage.toolActivities?.find(item => item.callId === event.toolCallId)
          appendStreamingTool(assistantMessage, {
            // 并非所有提供方都会先下发调用事件；结果事件自带的工具名兜底，
            // 避免调用行退化成占位的“工具”。调用行已带占位名时同样让位给真实名。
            name: (tool?.name && tool.name !== '工具' ? tool.name : undefined)
              || event.toolName
              || tool?.name
              || '工具',
            callId: event.toolCallId,
            result: event.content ?? '',
          })
        } else if (event.type === 'plan_updated') {
          // 每次事件携带完整计划快照，直接整体替换即可就位更新。
          const snapshot = parsePlanSnapshot(event.content)
          if (snapshot) assistantMessage.plan = snapshot
        } else if (event.type === 'compaction' && event.compaction) {
          const summaries = conversation.contextSummaries ||= []
          const index = summaries.findIndex(item => item.compressionId === event.compaction!.compressionId)
          if (index >= 0) summaries[index] = event.compaction
          else summaries.push(event.compaction)
        } else if (event.type === 'runtime' && event.runtime) {
          const runtime = event.runtime
          assistantMessage.metadata = {
            ...assistantMessage.metadata,
            extensions: {
              ...assistantMessage.metadata?.extensions,
              runtimeProvider: runtime.provider,
              runtimeProfileId: runtime.profileId || '',
              runtimeProfileName: runtime.profileName || '',
              runtimeAgentName: runtime.agentName || '',
              runtimeAgentVersion: runtime.agentVersion || '',
            },
          }
        } else if (event.type === 'done') {
          delete assistantMessage.executionStatus
          flushStream?.()
          receivedDone = true
          // 后端流式失败帧携带 status:"error"，映射为持久化的 Failed 状态，
          // 不能把非法字面量直接写进会话状态。
          const doneStatus = event.status === 'error' ? 'Failed' : (event.status || 'Completed')
          conversation.status = doneStatus as ConversationRecord['status']
          assistantMessage.tokenUsage = event.usage ?? undefined
          assistantMessage.modelId = event.modelId ?? undefined
        } else if (event.type === 'error') {
          flushStream?.()
          streamError = {
            title: event.error?.title,
            detail: event.error?.detail || 'Agent 执行失败',
            traceId: event.error?.traceId,
            code: event.error?.code,
          }
          throw new Error(streamError.detail)
        }
      }
      flushStream?.()
      if (!receivedDone && (!resume || receivedEvent)) throw new Error('流连接在完成事件前意外结束')
      try {
        const persisted = await api.getConversation(conversationId)
        if (resume && !receivedEvent && persisted.status === 'Running') throw new Error('当前会话没有可重连的执行，请重试')
        await options.hydrateFilePreviews(persisted)
        persisted.messages = mergeAssistantSnapshot(
          mergeOptimisticUserMessages(persisted.messages || [], conversation.messages || []),
          assistantMessage,
        )
        completedAgentId = persisted.agentId
        options.replaceConversation(persisted, conversationId)
      } catch (error) {
        if (resume) throw error
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
      if (streamedAssistant) delete streamedAssistant.executionStatus
      flushStream?.()
      const cancelReason = options.streams.getByRequest(requestId)?.cancelReason
      if (cancelReason && cancelReason !== 'network') {
        // Logout and refresh detach the subscriber, not the server execution.
        if (cancelReason === 'logout' || cancelReason === 'unload') return
        conversation.status = 'Cancelled'
        conversation.updatedAt = new Date().toISOString()
        conversation.lastMessageAt = conversation.updatedAt
        try {
          const persisted = await api.getConversation(conversationId)
          await options.hydrateFilePreviews(persisted)
          if (streamedAssistant) {
            persisted.messages = mergeAssistantSnapshot(
              mergeOptimisticUserMessages(persisted.messages || [], conversation.messages || []),
              streamedAssistant,
            )
          }
          if (persisted.messages?.length && persisted.status !== 'Running') {
            options.replaceConversation(persisted, conversationId)
          }
        } catch {
          // The server may not have persisted a newly cancelled conversation yet.
        }
        await options.refreshConversations(false)
      } else {
        // 排队已满（HTTP 429）与排队超时不是执行故障：换成面向用户的提示，用提示条而不是错误弹出。
        const queueNotice = streamError ? describeQueueTimeout(streamError) : describeQueueFull(error)
        if (!streamError) options.streams.cancelRequest(requestId, 'network')
        conversation.status = 'Failed'
        const lastMessage = conversation.messages?.at(-1)
        if (lastMessage?.role === 'assistant') {
          lastMessage.error = {
            title: queueNotice?.title ?? streamError?.title ?? 'Agent 执行失败',
            detail: queueNotice?.detail ?? streamError?.detail ?? (error instanceof Error ? error.message : '执行失败'),
            traceId: streamError?.traceId,
            code: queueNotice?.code ?? streamError?.code,
          }
        }
        try {
          const persisted = await api.getConversation(conversationId)
          await options.hydrateFilePreviews(persisted)
          if (streamedAssistant) {
            persisted.messages = mergeAssistantSnapshot(
              mergeOptimisticUserMessages(persisted.messages || [], conversation.messages || []),
              streamedAssistant,
            )
          }
          if (persisted.messages?.length && persisted.status !== 'Running') {
            options.replaceConversation(persisted, conversationId)
          }
        } catch {
          // Reconnect and conversation detail loading will recover persisted messages later.
        }
        if (options.selectedConversation.value?.conversationId === conversationId) {
          if (queueNotice) ElMessage.warning(queueNotice.detail)
          else options.notifyError(error)
        }
      }
    } finally {
      if (streamedAssistant) delete streamedAssistant.executionStatus
      contentQueue?.clear()
      options.streams.finish(requestId)
    }
  }

  function send(): Promise<void> {
    return runTurn()
  }

  function reconnect(conversation: ConversationRecord): Promise<void> {
    if (conversation.status !== 'Running' || options.streams.isStreaming(conversation.conversationId)) return Promise.resolve()
    return runTurn(conversation)
  }

  async function retry(): Promise<void> {
    const conversation = options.selectedConversation.value
    if (!conversation || !canRetry.value) return
    const input = lastUserMessage(conversation.messages)
    if (!input) return
    try {
      const persisted = await api.getConversation(conversation.conversationId)
      if (options.selectedConversation.value !== conversation || options.streams.isStreaming(conversation.conversationId)) return
      const persistedInput = lastUserMessage(persisted.messages)
      if (persisted.status === 'Running'
        || (persisted.status === 'Completed' && persistedInput?.sequence === input.sequence && persistedInput?.content === input.content)) {
        await options.hydrateFilePreviews(persisted)
        options.replaceConversation(persisted, conversation.conversationId)
        await reconnect(persisted)
        return
      }
    } catch (error) {
      // A rejected first request has no persisted conversation to reload.
      if (!(error && typeof error === 'object' && 'status' in error && error.status === 404)) {
        options.notifyError(error)
        return
      }
    }
    if (options.selectedConversation.value === conversation) await runTurn(undefined, input)
  }

  return { message, send, retry, canRetry, reconnect, stopStreaming, clearDraft }
}
