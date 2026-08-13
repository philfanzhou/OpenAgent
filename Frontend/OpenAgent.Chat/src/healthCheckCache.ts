export type CheckStatus = 'idle' | 'running' | 'ok' | 'warn' | 'error' | 'na'
export type CheckGroup = 'services' | 'infrastructure' | 'data'

export interface CheckItem {
  key: string
  group: CheckGroup
  name: string
  detail: string
  status: CheckStatus
  latencyMs?: number
  data?: Record<string, unknown>
}

export interface HealthCheckCache {
  /** 最近一次检测完成的时间（ISO 8601）。 */
  ranAt: string
  checks: CheckItem[]
}

export const HEALTH_CHECK_CACHE_KEY = 'openagent.health-check.cache'

/** 读取上一次检测的缓存结果；缓存缺失或损坏时返回 null。 */
export function loadHealthCheckCache(storage: Storage): HealthCheckCache | null {
  try {
    const raw = storage.getItem(HEALTH_CHECK_CACHE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as unknown
    if (!parsed || typeof parsed !== 'object') return null
    const cache = parsed as HealthCheckCache
    if (typeof cache.ranAt !== 'string' || !Array.isArray(cache.checks)) return null
    return cache
  } catch {
    return null
  }
}

/** 持久化最近一次检测结果；写入失败不影响检测结果展示。 */
export function saveHealthCheckCache(storage: Storage, cache: HealthCheckCache): void {
  try {
    storage.setItem(HEALTH_CHECK_CACHE_KEY, JSON.stringify(cache))
  } catch {
    // 写入失败时保持内存中的结果即可。
  }
}

/** 将缓存的检测结果对齐到当前种子项：丢弃已不存在的项，新增项保持待检测。 */
export function mergeChecksWithSeed(seed: CheckItem[], cached: CheckItem[]): CheckItem[] {
  const cachedByKey = new Map(cached.map(item => [item.key, item]))
  return seed.map(item => {
    const prior = cachedByKey.get(item.key)
    return prior ? { ...item, ...prior } : item
  })
}
