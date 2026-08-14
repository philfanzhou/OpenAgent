import { describe, expect, it } from 'vitest'
import { renderMarkdown } from './markdown'
import { buildDisplayMessages, fileLabel, formatFileSize, parseToolArguments } from './messagePresentation'
import type { ConversationMessage } from './types'

describe('message presentation', () => {
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

  it('keeps malformed tool arguments visible and formats file metadata', () => {
    expect(parseToolArguments('{bad json')).toBe('{bad json')
    expect(formatFileSize(1536)).toBe('2 KB')
    expect(fileLabel({ fileName: 'report.md', mediaType: 'text/markdown', length: 12 })).toBe('MD')
  })
})
