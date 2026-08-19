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

  it('limits render frequency while catching up with a large backlog', () => {
    vi.useFakeTimers()
    const updates: string[] = []
    const queue = createTypewriterQueue(content => updates.push(content))
    const content = 'x'.repeat(800)

    queue.enqueue(content)
    vi.advanceTimersByTime(160)

    expect(updates).toHaveLength(5)
    expect(updates.every(update => update.length <= 8)).toBe(true)
    queue.flush()
    expect(updates.join('')).toBe(content)
  })

  it('uses configurable timing and batch size', () => {
    vi.useFakeTimers()
    let displayed = ''
    const queue = createTypewriterQueue(content => { displayed += content }, {
      intervalMs: 50,
      charactersPerTick: 2,
      maxCharactersPerTick: 2,
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
