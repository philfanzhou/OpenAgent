import { describe, expect, it } from 'vitest'
import { renderMarkdown } from './markdown'
import { appendStreamingReasoning, appendStreamingTool, buildConversationTimeline, buildDisplayMessages, fileLabel, formatFileSize, mergeAssistantSnapshot, parseToolArguments, toolPresentation } from './messagePresentation'
import type { ContextSummary, ConversationMessage } from './types'

describe('message presentation', () => {
  it('keeps live reasoning split around an interleaved tool call', () => {
    const message: ConversationMessage = { messageId: 'stream', sequence: 1, role: 'assistant', content: '', timestamp: '' }

    appendStreamingReasoning(message, 'first ')
    appendStreamingReasoning(message, 'thought')
    appendStreamingTool(message, { name: 'search', callId: 'call-1' })
    appendStreamingTool(message, { name: 'search', callId: 'call-1', result: 'found' })
    appendStreamingReasoning(message, 'second thought')

    expect(message.processActivities).toEqual([
      { kind: 'reasoning', content: 'first thought' },
      { kind: 'tool', tool: { name: 'search', callId: 'call-1', result: 'found' } },
      { kind: 'reasoning', content: 'second thought' },
    ])
    expect(message.toolActivities).toEqual([{ name: 'search', callId: 'call-1', result: 'found' }])
  })

  it('renders common markdown while escaping raw HTML', () => {
    const html = renderMarkdown('# Result\n\n- **ready**\n\n```ts\nconst ok = true\n```\n\n<script>alert(1)</script>')

    expect(html).toContain('<h1>Result</h1>')
    expect(html).toContain('<strong>ready</strong>')
    expect(html).toContain('<code class="language-ts">')
    expect(html).not.toContain('<script>')
    expect(html).toContain('&lt;script&gt;')
  })

  it('adds safe attributes to rendered links and rejects javascript links', () => {
    const html = renderMarkdown('[docs](https://example.com) [unsafe](javascript:alert(1))')

    expect(html).toContain('target="_blank"')
    expect(html).toContain('rel="noopener noreferrer"')
    expect(html).not.toContain('href="javascript:')
  })

  it('renders authenticated blob image URLs used by file previews', () => {
    const html = renderMarkdown('![chart](blob:preview-image)')

    expect(html).toContain('<img src="blob:preview-image" alt="chart">')
  })

  it('blocks dangerous link protocols and keeps safe relative links', () => {
    const html = renderMarkdown(
      '[vb](vbscript:msgbox(1)) [data](data:text/html,%3Cscript%3Ealert(1)%3C/script%3E) [rel](./docs.md) [http](https://example.com)',
    )

    expect(html).not.toContain('href="vbscript:')
    expect(html).not.toContain('href="data:')
    expect(html).not.toContain('href="javascript:')
    expect(html).toContain('href="./docs.md"')
    expect(html).toContain('href="https://example.com"')
  })

  it('groups stored tool calls and results into the next assistant message', () => {
    const messages: ConversationMessage[] = [
      { messageId: '1', sequence: 1, role: 'assistant', content: '', timestamp: '', toolName: 'write_file', toolCallId: 'call-1', metadata: { ToolArguments: '{"path":"report.md"}' } },
      { messageId: '2', sequence: 2, role: 'tool', content: 'created', timestamp: '', toolCallId: 'call-1' },
      { messageId: '3', sequence: 3, role: 'assistant', content: 'Done', timestamp: '' },
    ]

    const result = buildDisplayMessages(messages)

    expect(result).toHaveLength(1)
    expect(result[0]?.toolActivities).toEqual([{ name: 'write_file', callId: 'call-1', arguments: { path: 'report.md' }, result: 'created' }])
  })

  it('merges every assistant phase in one turn without losing reasoning, calls, results, or content', () => {
    const messages: ConversationMessage[] = [
      { messageId: 'user-1', sequence: 1, role: 'user', content: 'Prepare the report', timestamp: '2026-08-19T01:00:00Z' },
      {
        messageId: 'assistant-1', sequence: 2, role: 'assistant', content: 'I will inspect the source.', timestamp: '2026-08-19T01:00:01Z',
        reasoning: 'Find the source.', toolName: 'load_skill', toolCallId: 'call-1', metadata: { ToolArguments: '{"name":"reports"}' },
      },
      { messageId: 'tool-1', sequence: 3, role: 'tool', content: 'Skill loaded', timestamp: '2026-08-19T01:00:02Z', toolCallId: 'call-1' },
      {
        messageId: 'assistant-2', sequence: 4, role: 'assistant', content: '', timestamp: '2026-08-19T01:00:03Z',
        reasoning: 'Write the report.', toolName: 'write_file', toolCallId: 'call-2', metadata: { ToolArguments: '{"path":"report.md"}' },
      },
      { messageId: 'tool-2', sequence: 5, role: 'tool', content: '{"created":true}', timestamp: '2026-08-19T01:00:04Z', toolCallId: 'call-2' },
      { messageId: 'assistant-3', sequence: 6, role: 'assistant', content: 'The report is ready.', timestamp: '2026-08-19T01:00:05Z' },
    ]

    const result = buildDisplayMessages(messages)

    expect(result).toHaveLength(2)
    expect(result[1]).toMatchObject({
      messageId: 'assistant-1',
      role: 'assistant',
      content: 'I will inspect the source.\nThe report is ready.',
      reasoning: 'Find the source.\nWrite the report.',
    })
    expect(result[1]?.toolActivities).toEqual([
      { name: 'load_skill', callId: 'call-1', arguments: { name: 'reports' }, result: 'Skill loaded' },
      { name: 'write_file', callId: 'call-2', arguments: { path: 'report.md' }, result: '{"created":true}' },
    ])
    expect(result[1]?.processActivities).toEqual([
      { kind: 'reasoning', content: 'Find the source.' },
      { kind: 'tool', tool: { name: 'load_skill', callId: 'call-1', arguments: { name: 'reports' }, result: 'Skill loaded' } },
      { kind: 'reasoning', content: 'Write the report.' },
      { kind: 'tool', tool: { name: 'write_file', callId: 'call-2', arguments: { path: 'report.md' }, result: '{"created":true}' } },
    ])

    const streamed: ConversationMessage = {
      messageId: 'stream', sequence: 2, role: 'assistant', timestamp: '2026-08-19T01:00:01Z',
      content: 'I will inspect the source.\nThe report is ready.',
      reasoning: 'Find the source.\nWrite the report.',
      toolActivities: [
        { name: 'load_skill', callId: 'call-1', arguments: { name: 'reports' } },
        { name: 'write_file', callId: 'call-2', arguments: { path: 'report.md' } },
      ],
    }
    expect(buildDisplayMessages(mergeAssistantSnapshot(messages, streamed))).toEqual(result)
    expect(buildDisplayMessages(result)).toEqual(result)
  })

  it('separates new assistant messages with a line break instead of concatenating them', () => {
    const messages: ConversationMessage[] = [
      { messageId: 'assistant-1', sequence: 1, role: 'assistant', content: 'First response', reasoning: 'First thought', timestamp: '' },
      { messageId: 'assistant-2', sequence: 2, role: 'assistant', content: 'Second response', reasoning: 'Second thought', timestamp: '' },
    ]

    expect(buildDisplayMessages(messages)[0]).toMatchObject({
      content: 'First response\nSecond response',
      reasoning: 'First thought\nSecond thought',
    })
  })

  it('keeps assistant executions separated by user turns', () => {
    const messages: ConversationMessage[] = [
      { messageId: 'assistant-1', sequence: 1, role: 'assistant', content: 'First answer', timestamp: '' },
      { messageId: 'user-1', sequence: 2, role: 'user', content: 'Follow-up', timestamp: '' },
      { messageId: 'assistant-2', sequence: 3, role: 'assistant', content: 'Second answer', timestamp: '' },
    ]

    expect(buildDisplayMessages(messages).map(message => message.content)).toEqual([
      'First answer',
      'Follow-up',
      'Second answer',
    ])
  })

  it('keeps incomplete tool history in one assistant message after cancellation', () => {
    const messages: ConversationMessage[] = [
      { messageId: 'call', sequence: 1, role: 'assistant', content: '', timestamp: '2026-08-19T01:00:00Z', toolName: 'read_file', toolCallId: 'call-1', metadata: { ToolArguments: '{bad json' } },
      { messageId: 'result', sequence: 2, role: 'tool', content: 'Cancelled by caller', timestamp: '2026-08-19T01:00:01Z', toolCallId: 'call-1' },
    ]

    const result = buildDisplayMessages(messages)

    expect(result).toHaveLength(1)
    expect(result[0]?.toolActivities).toEqual([
      { name: 'read_file', callId: 'call-1', arguments: '{bad json', result: 'Cancelled by caller' },
    ])
  })

  it('reconciles persisted tool results with the streamed snapshot and error', () => {
    const persisted: ConversationMessage[] = [
      {
        messageId: 'call', sequence: 1, role: 'assistant', content: '', timestamp: '', reasoning: 'Trying the write.',
        toolName: 'write_file', toolCallId: 'call-1', metadata: { ToolArguments: '{"path":"report.md"}' },
      },
      { messageId: 'result', sequence: 2, role: 'tool', content: 'Permission denied', timestamp: '', toolCallId: 'call-1' },
    ]
    const streamed: ConversationMessage = {
      messageId: 'stream', sequence: 1, role: 'assistant', content: 'Partial response', timestamp: '',
      reasoning: 'Trying the write.',
      toolActivities: [{ name: 'write_file', callId: 'call-1', arguments: { path: 'report.md' } }],
      error: { title: 'Execution failed', detail: 'Permission denied', traceId: 'trace-1' },
    }

    const result = buildDisplayMessages(mergeAssistantSnapshot(persisted, streamed))

    expect(result).toHaveLength(1)
    expect(result[0]).toMatchObject({
      content: 'Partial response',
      reasoning: 'Trying the write.',
      error: { title: 'Execution failed', detail: 'Permission denied', traceId: 'trace-1' },
    })
    expect(result[0]?.toolActivities).toEqual([
      { name: 'write_file', callId: 'call-1', arguments: { path: 'report.md' }, result: 'Permission denied' },
    ])
  })

  it('keeps malformed tool arguments visible and formats file metadata', () => {
    expect(parseToolArguments('{bad json')).toBe('{bad json')
    expect(formatFileSize(1536)).toBe('2 KB')
    expect(fileLabel({ fileName: 'report.md', mediaType: 'text/markdown', length: 12 })).toBe('MD')
  })

  it('labels MCP and Skill operations for conversation visibility', () => {
    expect(toolPresentation('mcp__local_tools__get_weather')).toEqual({
      kind: 'MCP',
      displayName: 'local_tools / get_weather',
    })
    expect(toolPresentation('load_skill')).toEqual({ kind: 'SKILL', displayName: '加载 Skill 指令' })
  })

  it('places a compaction summary at its source boundary instead of fixing it at the end', () => {
    const messages: ConversationMessage[] = [
      { messageId: 'user-1', sequence: 1, role: 'user', content: 'Before', timestamp: '2026-08-20T01:00:00Z' },
      { messageId: 'assistant-1', sequence: 2, role: 'assistant', content: 'Answer', timestamp: '2026-08-20T01:00:01Z' },
      { messageId: 'user-2', sequence: 3, role: 'user', content: 'After', timestamp: '2026-08-20T01:05:00Z' },
      { messageId: 'assistant-2', sequence: 4, role: 'assistant', content: 'Later answer', timestamp: '2026-08-20T01:05:01Z' },
    ]
    const summary: ContextSummary = {
      compressionId: 'compression-1', strategy: 'summarization', trigger: 'Manual', status: 'Succeeded',
      summary: 'Compact context', lastCompressedAt: '2026-08-20T01:02:00Z', compressedMessageCount: 2,
      originalStartSequence: 1, originalEndSequence: 2, originalTokenCount: 500, tokenCount: 200,
      originalHistoryRestored: false, sourceEndSequence: 2,
    }

    expect(buildConversationTimeline(messages, [summary]).map(item => item.kind === 'message'
      ? item.message.messageId
      : item.summary.compressionId)).toEqual([
      'user-1', 'assistant-1', 'compression-1', 'user-2', 'assistant-2',
    ])
  })

  it('keeps optimistic messages last when stored sequences have gaps from merged tool rows', () => {
    // 回答完成后本地只保留合并后的展示消息（tool 行被折叠、序号有空洞），
    // 乐观追加的新消息序号可能小于历史最大序号，排序必须保证其仍排在最后。
    const messages: ConversationMessage[] = [
      { messageId: 'user-new', sequence: 6, role: 'user', content: 'Third question', timestamp: '2026-08-20T02:00:00Z' },
      { messageId: 'user-2', sequence: 4, role: 'user', content: 'Second question', timestamp: '2026-08-20T01:05:00Z' },
      { messageId: 'assistant-merged', sequence: 2, role: 'assistant', content: 'First answer', timestamp: '2026-08-20T01:01:00Z' },
      { messageId: 'user-1', sequence: 1, role: 'user', content: 'First question', timestamp: '2026-08-20T01:00:00Z' },
      { messageId: 'assistant-new', sequence: 7, role: 'assistant', content: '', timestamp: '2026-08-20T02:00:01Z' },
      { messageId: 'assistant-2', sequence: 5, role: 'assistant', content: 'Second answer', timestamp: '2026-08-20T01:06:00Z' },
    ]

    expect(buildConversationTimeline(messages, []).map(item => item.kind === 'message'
      ? item.message.messageId
      : '')).toEqual([
      'user-1', 'assistant-merged', 'user-2', 'assistant-2', 'user-new', 'assistant-new',
    ])
  })
})
