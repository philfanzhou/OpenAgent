export interface TypewriterQueueOptions {
  intervalMs?: number
  charactersPerTick?: number
  /**
   * 积压追平速度：每个 tick 额外消化 pending/catchUpDivisor 个字符，
   * 即无论到达多快，全部积压都会在约 catchUpDivisor * intervalMs 毫秒内渲染完毕。
   */
  catchUpDivisor?: number
}

export interface TypewriterQueue {
  enqueue: (content: string) => void
  flush: () => void
  clear: () => void
}

const defaultOptions = {
  intervalMs: 32,
  charactersPerTick: 1,
  catchUpDivisor: 8,
} satisfies Required<TypewriterQueueOptions>

export function createTypewriterQueue(
  append: (content: string) => void,
  options: TypewriterQueueOptions = {},
): TypewriterQueue {
  const settings = { ...defaultOptions, ...options }
  const characters: string[] = []
  let offset = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  let ticksToEmpty = 0

  function pendingCount(): number {
    return characters.length - offset
  }

  function reset(): void {
    characters.length = 0
    offset = 0
  }

  function cancelTimer(): void {
    if (timer === undefined) return
    clearTimeout(timer)
    timer = undefined
  }

  function take(count: number): string {
    const content = characters.slice(offset, offset + count).join('')
    offset += count
    if (offset === characters.length) reset()
    return content
  }

  function schedule(): void {
    if (timer !== undefined || pendingCount() === 0) return
    timer = setTimeout(tick, settings.intervalMs)
  }

  function tick(): void {
    timer = undefined
    const pending = pendingCount()
    if (pending === 0) {
      ticksToEmpty = 0
      return
    }
    // 低速时逐字输出；出现积压则把剩余字符摊到剩余 tick 内线性消化，
    // 渲染节奏始终跟得上到达速度（约 catchUpDivisor 个 tick 追平）。
    ticksToEmpty = ticksToEmpty > 0 ? ticksToEmpty - 1 : settings.catchUpDivisor
    const planned = Math.ceil(pending / Math.max(ticksToEmpty, 1))
    const batchSize = Math.max(settings.charactersPerTick, planned)
    append(take(batchSize))
    schedule()
  }

  function enqueue(content: string): void {
    for (const character of content) characters.push(character)
    schedule()
  }

  function flush(): void {
    cancelTimer()
    const pending = pendingCount()
    if (pending > 0) append(take(pending))
  }

  function clear(): void {
    cancelTimer()
    reset()
  }

  return { enqueue, flush, clear }
}
