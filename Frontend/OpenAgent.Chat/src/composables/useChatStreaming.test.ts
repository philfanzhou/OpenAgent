import { describe, expect, it, vi, beforeEach } from 'vitest'
import { computed, ref } from 'vue'
import { useChatStreaming } from './useChatStreaming'
import { useConversationStreams } from './useConversationStreams'
import { api } from '../api'
import type { ConversationMessage, ConversationRecord } from '../types'

vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} })
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} })

vi.mock('../api', async () => {
  const actual = await vi.importActual<typeof import('../api')>('../api')
  return {
    api: {
      streamChat: vi.fn(),
      resumeChat: vi.fn(),
      cancelChat: vi.fn(),
      getConversation: vi.fn(),
    },
    makeLocalConversation: actual.makeLocalConversation,
  }
})

function row(partial: Partial<ConversationMessage> & { sequence: number; role: string; content: string }): ConversationMessage {
  return {
    messageId: 'srv-' + partial.sequence + '-' + Math.random().toString(36).slice(2, 6),
    timestamp: '2026-09-23T00:00:00.000Z',
    ...partial,
  } as ConversationMessage
}

function makeRecord(messages: ConversationMessage[], status: ConversationRecord['status']): ConversationRecord {
  return {
    conversationId: 'conv-1',
    tenantId: 'tenant-1',
    userId: 'user-1',
    agentId: 'agent-1',
    type: 0,
    status,
    version: 2,
    isDeletedByUser: false,
    createdAt: '2026-09-23T00:00:00.000Z',
    updatedAt: '2026-09-23T00:00:00.000Z',
    lastMessageAt: '2026-09-23T00:00:00.000Z',
    messageCount: messages.length,
    messages,
  }
}

async function setup(modelProfileId = 'profile-1', runtimeId = 'saved-runtime') {
  const streams = useConversationStreams()
  const conversations = ref<ConversationRecord[]>([])
  // 上一轮完成后的内存形态：合并展示消息（assistant 组携带末行序号 4，
  // 对应服务端原始行 user1/call2/tool3/text4）。
  const selectedConversation = ref<ConversationRecord | null>(makeRecord([
    row({ sequence: 1, role: 'user', content: '第一轮问题' }),
    row({
      sequence: 4, role: 'assistant', content: '第一轮回答',
      toolActivities: [{ name: 'search', callId: 'c1', result: '结果', arguments: {} }],
    }),
  ], 'Completed'))
  conversations.value = [selectedConversation.value!]

  const streaming = useChatStreaming({
    selectedAgentId: ref('agent-1'),
    selectedLlmProfileId: ref(modelProfileId),
    selectedRuntimeId: ref(runtimeId),
    agents: ref([]),
    conversations,
    selectedConversation,
    selectedConversationStreaming: computed(() => false),
    pendingFiles: ref([]),
    streams,
    hydrateFilePreviews: async () => {},
    replaceConversation: (detail, previous) => {
      const list = conversations.value
      const index = list.findIndex(item =>
        item.conversationId === previous || item.conversationId === detail.conversationId)
      if (index >= 0) list[index] = detail
      else list.unshift(detail)
      if (selectedConversation.value?.conversationId === previous) selectedConversation.value = detail
    },
    refreshConversations: async () => {},
    notifyError: () => {},
  })
  return { streams, selectedConversation, streaming }
}

describe('useChatStreaming', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('requires an independently saved model connection', async () => {
    const { streaming } = await setup('')
    streaming.message.value = '运行'
    await streaming.send()
    expect(api.streamChat).not.toHaveBeenCalled()
  })

  it('keeps the server-reported runtime after reloading the persisted response', async () => {
    const { streaming, selectedConversation } = await setup()
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'runtime', runtime: { provider: 'OpenCode', profileId: 'actual-runtime', agentVersion: '1.2.3' } }
      yield { type: 'content', content: '完成' }
      yield { type: 'done', conversationId: 'conv-1' }
    })() as never)
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 5, role: 'user', content: '运行' }),
      row({ sequence: 6, role: 'assistant', content: '完成', metadata: { extensions: {
        runtimeProvider: 'OpenCode', runtimeProfileId: 'actual-runtime', runtimeAgentVersion: '1.2.3',
      } } }),
    ], 'Completed') as never)
    streaming.message.value = '运行'
    await streaming.send()
    expect(selectedConversation.value?.messages?.at(-1)?.metadata?.extensions).toMatchObject({
      runtimeProvider: 'OpenCode', runtimeProfileId: 'actual-runtime', runtimeAgentVersion: '1.2.3',
    })
  })

  it('updates a native compaction in place and preserves the persisted audit after reload', async () => {
    const { streaming, selectedConversation } = await setup()
    const audit = {
      compressionId: 'native-1', strategy: 'runtime', trigger: 'Automatic', status: 'Succeeded',
      summary: 'retained state', isRuntimeEvent: true, lastCompressedAt: '2026-11-13T00:00:00Z',
      compressedMessageCount: 0, originalStartSequence: 0, originalEndSequence: 0,
      originalTokenCount: 0, tokenCount: 0, originalHistoryRestored: false, sourceEndSequence: 5,
    }
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'compaction', compaction: { ...audit, status: 'InProgress', summary: null } }
      yield { type: 'compaction', compaction: audit }
      expect(selectedConversation.value?.contextSummaries).toEqual([audit])
      yield { type: 'done', conversationId: 'conv-1' }
    })() as never)
    vi.mocked(api.getConversation).mockResolvedValue({ ...makeRecord([], 'Completed'), contextSummaries: [audit] } as never)
    streaming.message.value = '继续'
    await streaming.send()
    expect(selectedConversation.value?.contextSummaries).toEqual([audit])
  })

  it('passes the saved runtime independently from Agent and model selection', async () => {
    const { streaming } = await setup('profile-1', 'saved-opencode')
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'done', conversationId: 'conv-1' }
    })() as never)
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([], 'Completed') as never)
    streaming.message.value = '运行'
    await streaming.send()
    expect(vi.mocked(api.streamChat).mock.calls[0]?.[1]).toBe('agent-1')
    expect(vi.mocked(api.streamChat).mock.calls[0]?.[2]).toBe('profile-1')
    expect(vi.mocked(api.streamChat).mock.calls[0]?.[7]).toBe('saved-opencode')
  })

  it('does not send when the Runtime picker has no saved selection', async () => {
    const { streaming } = await setup('profile-1', '')
    streaming.message.value = '运行'
    await streaming.send()
    expect(api.streamChat).not.toHaveBeenCalled()
  })

  it('keeps every turn intact after a user stop once the server persisted the cancelled turn', async () => {
    const { streams, selectedConversation, streaming } = await setup()
    let onAbort: (() => void) | undefined
    vi.mocked(api.streamChat).mockImplementation((...args: unknown[]) => {
      const signal = args[6] as AbortSignal | undefined
      signal?.addEventListener('abort', () => onAbort?.(), { once: true })
      return (async function* () {
        yield { type: 'conversation', conversationId: 'conv-1' }
        yield { type: 'tool_call', toolName: 'search', toolCallId: 'c2', toolArguments: { q: 'x' } }
        await new Promise<void>(resolve => { onAbort = resolve })
        throw new DOMException('aborted', 'AbortError')
      })() as never
    })
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 1, role: 'user', content: '第一轮问题' }),
      row({ sequence: 2, role: 'assistant', content: '', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 3, role: 'tool', content: '结果', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 4, role: 'assistant', content: '第一轮回答' }),
      row({ sequence: 5, role: 'user', content: '第二轮问题' }),
      row({ sequence: 6, role: 'assistant', content: '', toolCallId: 'c2', toolName: 'search' }),
      row({ sequence: 7, role: 'tool', content: '第二轮结果', toolCallId: 'c2', toolName: 'search' }),
      row({ sequence: 8, role: 'assistant', content: '第二轮部分回答', metadata: { executionStatus: 'Cancelled' } }),
    ], 'Cancelled') as never)

    streaming.message.value = '第二轮问题'
    const sendPromise = streaming.send()
    await new Promise(resolve => setTimeout(resolve, 20))
    streams.cancelConversation('conv-1', 'user')
    await sendPromise.catch(() => {})

    const messages = selectedConversation.value?.messages || []
    expect(messages.map(item => `${item.role}:${item.sequence}:${item.content}`)).toEqual([
      'user:1:第一轮问题',
      'assistant:4:第一轮回答',
      'user:5:第二轮问题',
      'assistant:8:第二轮部分回答',
    ])
    expect(messages[1]?.toolActivities?.map(tool => tool.name)).toEqual(['search'])
    expect(messages[3]?.toolActivities?.map(tool => tool.name)).toEqual(['search'])
  })

  it('keeps the optimistic turn visible after a user stop that races a stale server snapshot', async () => {
    const { streams, selectedConversation, streaming } = await setup()
    let onAbort: (() => void) | undefined
    vi.mocked(api.streamChat).mockImplementation((...args: unknown[]) => {
      const signal = args[6] as AbortSignal | undefined
      signal?.addEventListener('abort', () => onAbort?.(), { once: true })
      return (async function* () {
        yield { type: 'conversation', conversationId: 'conv-1' }
        yield { type: 'tool_call', toolName: 'search', toolCallId: 'c2', toolArguments: { q: 'x' } }
        yield { type: 'content', content: '第二轮部分回答' }
        yield { type: 'tool_result', toolCallId: 'c2', toolName: 'search', content: '第二轮结果' }
        await new Promise<void>(resolve => { onAbort = resolve })
        throw new DOMException('aborted', 'AbortError')
      })() as never
    })
    // 停止后立即拉取：服务端尚未持久化本轮，只返回上一轮的行。
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 1, role: 'user', content: '第一轮问题' }),
      row({ sequence: 2, role: 'assistant', content: '', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 3, role: 'tool', content: '结果', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 4, role: 'assistant', content: '第一轮回答' }),
    ], 'Completed') as never)

    streaming.message.value = '第二轮问题'
    const sendPromise = streaming.send()
    await new Promise(resolve => setTimeout(resolve, 20))
    streams.cancelConversation('conv-1', 'user')
    await sendPromise.catch(() => {})

    const messages = selectedConversation.value?.messages || []
    expect(messages.map(item => `${item.role}:${item.sequence}:${item.content}`)).toEqual([
      'user:1:第一轮问题',
      'assistant:4:第一轮回答',
      'user:5:第二轮问题',
      'assistant:6:第二轮部分回答',
    ])
    expect(messages[1]?.toolActivities?.map(tool => tool.name)).toEqual(['search'])
    expect(messages[3]?.toolActivities?.map(tool => `${tool.name}:${tool.result}`)).toEqual(['search:第二轮结果'])
  })

  it('preserves tool names through streaming and the post-done history replacement', async () => {
    const { selectedConversation, streaming } = await setup()
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'conversation', conversationId: 'conv-1' }
      yield { type: 'tool_call', toolName: 'read_file', toolCallId: 'call-1', toolArguments: { fileId: 'f1' } }
      yield { type: 'tool_result', toolCallId: 'call-1', toolName: 'read_file', content: '文件内容' }
      yield { type: 'content', content: '回答正文' }
      yield { type: 'done', status: 'Completed', usage: null, modelId: 'glm-5.3' }
    })() as never)
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 1, role: 'user', content: '第一轮问题' }),
      row({ sequence: 2, role: 'assistant', content: '', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 3, role: 'tool', content: '结果', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 4, role: 'assistant', content: '第一轮回答' }),
      row({ sequence: 5, role: 'user', content: '读一下文件' }),
      row({ sequence: 6, role: 'assistant', content: '', toolCallId: 'call-1', toolName: 'read_file', metadata: { toolArguments: '{"fileId":"f1"}' } }),
      row({ sequence: 7, role: 'tool', content: '文件内容', toolCallId: 'call-1', toolName: 'read_file' }),
      row({ sequence: 8, role: 'assistant', content: '回答正文', modelId: 'glm-5.3' }),
    ], 'Completed') as never)

    streaming.message.value = '读一下文件'
    await streaming.send()

    const assistant = selectedConversation.value?.messages?.filter(item => item.role === 'assistant').at(-1)
    expect(assistant?.toolActivities?.map(tool => tool.name)).toEqual(['read_file'])
    expect(assistant?.processActivities?.map(item => item.kind === 'tool' ? item.tool.name : 'reasoning')).toEqual(['read_file'])
  })

  it('repairs a placeholder tool name when the result event carries the real one', async () => {
    const { selectedConversation, streaming } = await setup()
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'conversation', conversationId: 'conv-1' }
      // 调用帧缺名（如旧版引擎/部分网关）：行先退化为“工具”
      yield { type: 'tool_call', toolCallId: 'call-9', toolArguments: { fileId: 'f1' } }
      // 结果帧自带工具名：必须把占位名修复为真实名
      yield { type: 'tool_result', toolCallId: 'call-9', toolName: 'read_file', content: '文件内容' }
      yield { type: 'content', content: '回答正文' }
      yield { type: 'done', status: 'Completed', usage: null, modelId: 'glm-5.3' }
    })() as never)
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 1, role: 'user', content: '第一轮问题' }),
      row({ sequence: 2, role: 'assistant', content: '', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 3, role: 'tool', content: '结果', toolCallId: 'c1', toolName: 'search' }),
      row({ sequence: 4, role: 'assistant', content: '第一轮回答' }),
      row({ sequence: 5, role: 'user', content: '读一下文件' }),
      row({ sequence: 8, role: 'assistant', content: '回答正文', modelId: 'glm-5.3' }),
    ], 'Completed') as never)

    streaming.message.value = '读一下文件'
    await streaming.send()

    const assistant = selectedConversation.value?.messages?.filter(item => item.role === 'assistant').at(-1)
    expect(assistant?.toolActivities?.map(tool => tool.name)).toEqual(['read_file'])
  })
  it('retries the failed input and its attachments in the same conversation without consuming a new draft', async () => {
    const { streaming, selectedConversation } = await setup()
    const failed = makeRecord([
      row({ sequence: 5, role: 'user', content: '分析附件', files: [{ fileId: 'file-1', fileName: 'report.txt', mediaType: 'text/plain', length: 10 }] }),
      row({ sequence: 6, role: 'assistant', content: '', error: { detail: '模型不可用' } }),
    ], 'Failed')
    selectedConversation.value = failed
    vi.mocked(api.getConversation).mockResolvedValue(failed)
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'content', content: '附件分析完成' }
      yield { type: 'done' }
    })())
    streaming.message.value = '尚未发送的草稿'

    await streaming.retry()

    expect(api.streamChat).toHaveBeenCalledWith('分析附件', 'agent-1', 'profile-1', 'conv-1', ['file-1'], 'conv-1', expect.any(AbortSignal), 'saved-runtime')
    expect(streaming.message.value).toBe('尚未发送的草稿')
  })

  it('reattaches instead of resubmitting when a connection error hid a running turn', async () => {
    const { streaming, selectedConversation } = await setup()
    selectedConversation.value = makeRecord([
      row({ sequence: 5, role: 'user', content: '正在运行的问题' }),
      row({ sequence: 6, role: 'assistant', content: '', error: { detail: '连接中断' } }),
    ], 'Failed')
    const running = makeRecord([row({ sequence: 5, role: 'user', content: '正在运行的问题' })], 'Running')
    const completed = makeRecord([
      ...running.messages!, row({ sequence: 6, role: 'assistant', content: '完整结果' }),
    ], 'Completed')
    vi.mocked(api.getConversation).mockResolvedValueOnce(running).mockResolvedValue(completed)
    vi.mocked(api.resumeChat).mockImplementation(() => (async function* () {
      yield { type: 'content', content: '完整结果' }
      yield { type: 'done' }
    })())

    await streaming.retry()

    expect(api.streamChat).not.toHaveBeenCalled()
    expect(api.resumeChat).toHaveBeenCalledWith('conv-1', expect.any(AbortSignal))
    expect(selectedConversation.value?.messages?.map(item => item.content)).toEqual(['正在运行的问题', '完整结果'])
  })

  it('replays a running turn without duplicating partial content or losing earlier turns even before settings load', async () => {
    const { streaming, selectedConversation } = await setup('', '')
    const running = makeRecord([
      row({ sequence: 1, role: 'user', content: '第一轮问题' }),
      row({ sequence: 4, role: 'assistant', content: '第一轮回答' }),
      row({ sequence: 5, role: 'user', content: '第二轮问题' }),
      row({ sequence: 6, role: 'assistant', content: '部分' }),
    ], 'Running')
    selectedConversation.value = running
    vi.mocked(api.resumeChat).mockImplementation(() => (async function* () {
      yield { type: 'content', content: '部分' }
      yield { type: 'content', content: '完整回答' }
      yield { type: 'done' }
    })())
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      ...running.messages!.slice(0, 3), row({ sequence: 6, role: 'assistant', content: '部分完整回答' }),
    ], 'Completed'))

    await streaming.reconnect(selectedConversation.value)

    expect(api.streamChat).not.toHaveBeenCalled()
    expect(selectedConversation.value?.messages?.map(item => item.content)).toEqual(['第一轮问题', '第一轮回答', '第二轮问题', '部分完整回答'])
    expect(streaming.canRetry.value).toBe(false)
  })

  it('loads the completed result when execution finishes between detail loading and reconnect', async () => {
    const { streaming, selectedConversation } = await setup()
    selectedConversation.value!.status = 'Running'
    vi.mocked(api.resumeChat).mockImplementation(() => (async function* () {})())
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 1, role: 'user', content: '第一轮问题' }),
      row({ sequence: 4, role: 'assistant', content: '后端已完成的结果' }),
    ], 'Completed'))

    await streaming.reconnect(selectedConversation.value!)

    expect(selectedConversation.value?.status).toBe('Completed')
    expect(selectedConversation.value?.messages?.at(-1)?.content).toBe('后端已完成的结果')
    expect(api.streamChat).not.toHaveBeenCalled()
  })

  it('keeps retry available when a resumed stream ends before its terminal event', async () => {
    const { streaming, selectedConversation } = await setup()
    selectedConversation.value!.status = 'Running'
    vi.mocked(api.resumeChat).mockImplementation(() => (async function* () {
      yield { type: 'content', content: '中断的部分回答' }
    })())
    vi.mocked(api.getConversation).mockRejectedValue(new Error('网络不可用'))

    await streaming.reconnect(selectedConversation.value!)

    expect(selectedConversation.value?.status).toBe('Failed')
    expect(selectedConversation.value?.messages?.at(-1)?.error?.detail).toBe('流连接在完成事件前意外结束')
    expect(streaming.canRetry.value).toBe(true)
  })

  it('detaches on logout without marking the server turn cancelled or restoring private data', async () => {
    const { streaming, selectedConversation, streams } = await setup()
    let ready!: () => void
    const started = new Promise<void>(resolve => { ready = resolve })
    vi.mocked(api.streamChat).mockImplementation((...args) => (async function* () {
      const signal = args[6]!
      yield { type: 'content', content: '生成中' }
      await new Promise<void>((_, reject) => {
        signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true })
        ready()
      })
    })())
    streaming.message.value = '继续运行'
    const pending = streaming.send()
    await started
    streams.cancelAll('logout')
    await pending

    expect(selectedConversation.value?.status).toBe('Running')
    expect(api.getConversation).not.toHaveBeenCalled()
    expect(api.cancelChat).not.toHaveBeenCalled()
  })

  it('explicitly cancels the server execution before detaching the subscriber on stop', async () => {
    const { streaming, streams } = await setup()
    const state = streams.start('conv-1')
    vi.mocked(api.cancelChat).mockImplementation(async () => {
      expect(state.controller.signal.aborted).toBe(false)
    })
    await streaming.stopStreaming()
    expect(api.cancelChat).toHaveBeenCalledWith('conv-1')
    expect(state.controller.signal.aborted).toBe(true)
    expect(state.cancelReason).toBe('user')
    streams.finish(state.requestId)
  })

  it('treats a producer cancellation arriving before the cancel HTTP reply as an intentional stop', async () => {
    const { streaming, selectedConversation } = await setup()
    let cancelProducer: (() => void) | undefined
    vi.mocked(api.streamChat).mockImplementation(() => (async function* () {
      yield { type: 'content', content: '部分结果' }
      await new Promise<void>((_, reject) => { cancelProducer = () => reject(new DOMException('cancelled', 'AbortError')) })
    })())
    vi.mocked(api.getConversation).mockResolvedValue(makeRecord([
      row({ sequence: 5, role: 'user', content: '停止这个问题' }),
      row({ sequence: 6, role: 'assistant', content: '部分结果', metadata: { executionStatus: 'Cancelled' } }),
    ], 'Cancelled'))
    streaming.message.value = '停止这个问题'
    const pending = streaming.send()
    await vi.waitFor(() => expect(cancelProducer).toBeTypeOf('function'))
    vi.mocked(api.cancelChat).mockImplementation(async () => {
      cancelProducer!()
      await pending
    })

    await streaming.stopStreaming()

    expect(selectedConversation.value?.status).toBe('Cancelled')
    expect(selectedConversation.value?.messages?.at(-1)?.error).toBeUndefined()
    expect(streaming.canRetry.value).toBe(false)
  })

})
