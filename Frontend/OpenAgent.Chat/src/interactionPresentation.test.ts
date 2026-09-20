import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  classifyInteraction,
  collectAllInteractions,
  copyInteractionText,
  formatInteractionTime,
  interactionStatusLabel,
  interactionStatusTagType,
  prettyInteractionPayload,
  toolCategory,
} from './interactionPresentation'
import type { LlmInteractionRecord } from './types'

function record(interactionId: string): LlmInteractionRecord {
  return {
    interactionId,
    tenantId: 'development',
    userId: 'anonymous',
    conversationId: 'conv-1',
    traceId: 'trace-1',
    agentId: 'default',
    source: 0,
    provider: 'kimi',
    apiFormat: 'OpenAIChatCompletions',
    modelId: 'kimi-k2.6',
    streamed: true,
    callIndex: 0,
    requestJson: '{}',
    responseJson: '{}',
    tokenUsage: { promptTokens: 1, completionTokens: 2, totalTokens: 3 },
    status: 0,
    errorMessage: null,
    startedAt: '2026-09-20T01:47:00.187764+00:00',
    durationMs: 91,
  }
}

describe('collectAllInteractions', () => {
  it('aggregates pages until a short page arrives', async () => {
    const pages = [
      Array.from({ length: 2 }, (_, index) => record(`a-${index}`)),
      Array.from({ length: 2 }, (_, index) => record(`b-${index}`)),
      [record('c-0')],
    ]
    const calls: Array<[number, number]> = []
    const records = await collectAllInteractions(async (skip, take) => {
      calls.push([skip, take])
      return pages[skip / take] ?? []
    }, 2)

    expect(records.map(item => item.interactionId)).toEqual(['a-0', 'a-1', 'b-0', 'b-1', 'c-0'])
    expect(calls).toEqual([[0, 2], [2, 2], [4, 2]])
  })

  it('stops at maxPages even if the endpoint keeps returning full pages', async () => {
    const records = await collectAllInteractions(async () => [record('x')], 1, 3)
    expect(records).toHaveLength(3)
  })
})

describe('interaction labels', () => {
  it('maps status enums to display text', () => {
    expect(interactionStatusLabel(0)).toBe('成功')
    expect(interactionStatusLabel(1)).toBe('失败')
    expect(interactionStatusLabel(2)).toBe('已取消')
    expect(interactionStatusTagType(0)).toBe('success')
    expect(interactionStatusTagType(1)).toBe('danger')
    expect(interactionStatusTagType(2)).toBe('info')
  })
})

describe('toolCategory', () => {
  it('maps builtin tools to fine-grained categories and MCP tools to their server', () => {
    expect(toolCategory('execute_code')).toBe('代码执行')
    expect(toolCategory('read_file')).toBe('文件')
    expect(toolCategory('create_file_transfer_url')).toBe('文件')
    expect(toolCategory('load_skill')).toBe('技能')
    expect(toolCategory('run_skill_script')).toBe('技能')
    expect(toolCategory('get_current_user_profile')).toBe('用户信息')
    expect(toolCategory('search_knowledge_base')).toBe('知识库')
    expect(toolCategory('mcp__github__create_issue')).toBe('MCP·github')
    expect(toolCategory('mcp__x')).toBe('MCP')
    expect(toolCategory('custom_unknown')).toBe('其他')
  })
})

describe('classifyInteraction', () => {
  function payloadRecord(fields: { source?: number; request?: unknown; response?: unknown }): LlmInteractionRecord {
    return {
      ...record('c-1'),
      source: fields.source ?? 0,
      requestJson: fields.request === undefined ? undefined : JSON.stringify(fields.request),
      responseJson: fields.response === undefined ? undefined : JSON.stringify(fields.response),
    }
  }

  it('classifies compaction source as its own category', () => {
    const result = classifyInteraction(payloadRecord({ source: 1 }))
    expect(result.primary).toBe('压缩摘要')
    expect(result.tags).toEqual([])
  })

  it('classifies model tool calls with deduped fine-grained tags', () => {
    const result = classifyInteraction(payloadRecord({
      response: { messages: [{ contents: [
        { kind: 'functionCall', name: 'load_skill' },
        { kind: 'functionCall', name: 'run_skill_script' },
        { kind: 'functionCall', name: 'read_file' },
        { kind: 'functionCall', name: 'mcp__github__create_issue' },
      ] }] },
    }))
    expect(result.primary).toBe('工具调用')
    expect(result.tags).toEqual(['技能', '文件', 'MCP·github'])
    expect(result.toolNames).toEqual(['load_skill', 'run_skill_script', 'read_file', 'mcp__github__create_issue'])
  })

  it('classifies answer calls fed with tool results as chat after tools', () => {
    const result = classifyInteraction(payloadRecord({
      request: { messages: [
        { contents: [{ kind: 'text', text: 'q' }] },
        { contents: [{ kind: 'functionResult', callId: 'c1', result: 'ok' }] },
      ] },
      response: { messages: [{ contents: [{ kind: 'text', text: 'answer' }] }] },
    }))
    expect(result.primary).toBe('会话')
    expect(result.tags).toEqual(['工具后'])
  })

  it('classifies plain turns as chat and flags image data contents', () => {
    const plain = classifyInteraction(payloadRecord({
      request: { messages: [{ contents: [{ kind: 'text', text: 'hi' }] }] },
      response: { messages: [{ contents: [{ kind: 'text', text: 'ok' }] }] },
    }))
    expect(plain.primary).toBe('会话')
    expect(plain.tags).toEqual([])

    const withImage = classifyInteraction(payloadRecord({
      request: { messages: [{ contents: [
        { kind: 'text', text: 'look' },
        { kind: 'data', mediaType: 'image/png', bytes: 70 },
      ] }] },
      response: { messages: [{ contents: [{ kind: 'text', text: 'ok' }] }] },
    }))
    expect(withImage.primary).toBe('会话')
    expect(withImage.tags).toEqual(['图片'])
  })

  it('degrades to source-based classification when payloads are truncated', () => {
    const result = classifyInteraction({ ...record('c-9'), requestJson: '{"a":"…', responseJson: '{"b":"…' })
    expect(result.primary).toBe('会话')
    expect(result.tags).toEqual([])
  })
})

describe('trace and time formatting', () => {
  it('formats interaction timestamps as local time', () => {
    expect(formatInteractionTime('2026-09-20T01:47:00Z')).toMatch(/^\d{2}:\d{2}:\d{2}$/)
    expect(formatInteractionTime('not-a-date')).toBe('not-a-date')
  })
})

describe('prettyInteractionPayload', () => {
  it('pretty-prints valid JSON payloads', () => {
    expect(prettyInteractionPayload('{"a":1}')).toBe('{\n  "a": 1\n}')
  })

  it('returns truncated payloads as-is when they no longer parse', () => {
    expect(prettyInteractionPayload('{"a":"…')).toBe('{"a":"…')
    expect(prettyInteractionPayload(null)).toBe('')
    expect(prettyInteractionPayload(undefined)).toBe('')
  })
})

describe('copyInteractionText', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('uses the Clipboard API when available', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { clipboard: { writeText } })

    await copyInteractionText('trace-1')

    expect(writeText).toHaveBeenCalledExactlyOnceWith('trace-1')
  })

  it('falls back to execCommand when the Clipboard API rejects', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('Document is not focused'))
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    const fakeDocument = {
      createElement: () => ({
        value: '', style: {}, removed: false,
        setAttribute: vi.fn(), select: vi.fn(),
        remove: vi.fn(function (this: { removed: boolean }) { this.removed = true }),
      }),
      body: { appendChild: vi.fn() },
      execCommand: vi.fn(() => true),
    }
    vi.stubGlobal('document', fakeDocument)

    await copyInteractionText('trace-2')

    expect(writeText).toHaveBeenCalledOnce()
    expect(fakeDocument.execCommand).toHaveBeenCalledWith('copy')
  })

  it('falls back to a hidden textarea with execCommand when the API is missing', async () => {
    const textareas: Array<{ value: string; removed: boolean }> = []
    const fakeDocument = {
      createElement: () => {
        const area = {
          value: '',
          removed: false,
          style: {},
          setAttribute: vi.fn(),
          select: vi.fn(),
          remove: vi.fn(function (this: { removed: boolean }) { this.removed = true }),
        }
        textareas.push(area)
        return area
      },
      body: { appendChild: vi.fn() },
      execCommand: vi.fn(() => true),
    }
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', fakeDocument)

    await copyInteractionText('payload')

    expect(fakeDocument.body.appendChild).toHaveBeenCalledOnce()
    expect(fakeDocument.execCommand).toHaveBeenCalledWith('copy')
    expect(textareas[0]?.value).toBe('payload')
    expect(textareas[0]?.removed).toBe(true)
  })

  it('rejects when both clipboard paths are unavailable', async () => {
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('document', { createElement: undefined })

    await expect(copyInteractionText('x')).rejects.toThrow('Clipboard is unavailable')
  })
})
