import { afterEach, describe, expect, it, vi } from 'vitest'
import { createTypewriterQueue } from './typewriterQueue'

afterEach(() => {
  vi.useRealTimers()
})

describe('typewriter queue', () => {
  it('displays chunk content once and in order', () => {
    vi.useFakeTimers()
    let displayed = ''
    const queue = createTypewriterQueue(content => { displayed += content })

    queue.enqueue('你')
    queue.enqueue('好 A😀')

    vi.runAllTimers()
    expect(displayed).toBe('你好 A😀')
  })

  it('accelerates rendering to drain a large backlog within the catch-up window', () => {
    vi.useFakeTimers()
    const updates: string[] = []
    const queue = createTypewriterQueue(content => updates.push(content))
    const content = 'x'.repeat(800)

    queue.enqueue(content)
    // 默认 catchUpDivisor=8、intervalMs=32：剩余字符摊到最多 8 个 tick，恰好 ~256ms 追平。
    vi.advanceTimersByTime(32)
    expect(updates.reduce((total, update) => total + update.length, 0)).toBe(100)
    vi.advanceTimersByTime(32 * 7)
    expect(updates.reduce((total, update) => total + update.length, 0)).toBe(800)
    queue.flush()
    expect(updates.join('')).toBe(content)
  })

  it('keeps character-by-character pacing when the stream is slow', () => {
    vi.useFakeTimers()
    const updates: string[] = []
    const queue = createTypewriterQueue(content => updates.push(content), {
      charactersPerTick: 1,
      catchUpDivisor: 8,
    })

    for (const character of 'abc') queue.enqueue(character)
    vi.advanceTimersByTime(96)
    expect(updates).toEqual(['a', 'b', 'c'])
  })

  it('uses configurable timing and batch size', () => {
    vi.useFakeTimers()
    let displayed = ''
    const queue = createTypewriterQueue(content => { displayed += content }, {
      intervalMs: 50,
      charactersPerTick: 2,
      catchUpDivisor: 4,
    })

    queue.enqueue('abcd')
    vi.advanceTimersByTime(49)
    expect(displayed).toBe('')
    vi.advanceTimersByTime(1)
    expect(displayed).toBe('ab')
    vi.advanceTimersByTime(50)
    expect(displayed).toBe('abcd')
  })

  it('flushes pending characters and cancels scheduled updates', () => {
    vi.useFakeTimers()
    const updates: string[] = []
    const queue = createTypewriterQueue(content => updates.push(content))

    queue.enqueue('complete response')
    queue.flush()
    vi.runAllTimers()

    expect(updates).toEqual(['complete response'])
  })

  it('clears an old response before accepting new content', () => {
    vi.useFakeTimers()
    let displayed = ''
    const queue = createTypewriterQueue(content => { displayed += content })

    queue.enqueue('old response')
    queue.clear()
    queue.enqueue('new')
    vi.runAllTimers()

    expect(displayed).toBe('new')
  })

  it('keeps concurrent conversation queues isolated', () => {
    vi.useFakeTimers()
    let conversationA = ''
    let conversationB = ''
    const queueA = createTypewriterQueue(content => { conversationA += content })
    const queueB = createTypewriterQueue(content => { conversationB += content })

    queueA.enqueue('alpha')
    queueB.enqueue('beta')
    queueA.flush()
    queueB.flush()

    expect(conversationA).toBe('alpha')
    expect(conversationB).toBe('beta')
  })
})
