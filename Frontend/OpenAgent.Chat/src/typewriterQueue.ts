export interface TypewriterQueueOptions {
  intervalMs?: number
  charactersPerTick?: number
  catchUpThreshold?: number
  maxCharactersPerTick?: number
}

export interface TypewriterQueue {
  enqueue: (content: string) => void
  flush: () => void
  clear: () => void
}

const defaultOptions = {
  intervalMs: 32,
  charactersPerTick: 1,
  catchUpThreshold: 80,
  maxCharactersPerTick: 8,
} satisfies Required<TypewriterQueueOptions>

export function createTypewriterQueue(
  append: (content: string) => void,
  options: TypewriterQueueOptions = {},
): TypewriterQueue {
  const settings = { ...defaultOptions, ...options }
  const characters: string[] = []
  let offset = 0
  let timer: ReturnType<typeof setTimeout> | undefined

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
    if (pending === 0) return
    const batchSize = Math.min(
      settings.maxCharactersPerTick,
      Math.max(settings.charactersPerTick, Math.ceil(pending / settings.catchUpThreshold)),
    )
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
