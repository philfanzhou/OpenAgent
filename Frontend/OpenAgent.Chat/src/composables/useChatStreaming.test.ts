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

async function setup() {
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
    selectedLlmProfileId: ref('profile-1'),
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

  it('keeps every turn intact after a user stop once the server persisted the cancelled turn', async () => {
    const { streams, selectedConversation, streaming } = await setup()
    let onAbort: (() => void) | undefined
    vi.mocked(api.streamChat).mockImplementation((...args: unknown[]) => {
      const signal = args.at(-1) as AbortSignal | undefined
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
      const signal = args.at(-1) as AbortSignal | undefined
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
})
