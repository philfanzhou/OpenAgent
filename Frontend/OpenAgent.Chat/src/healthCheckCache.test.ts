import { beforeEach, describe, expect, it, vi } from 'vitest'
import { loadHealthCheckCache, mergeChecksWithSeed, saveHealthCheckCache, type CheckItem, type HealthCheckCache } from './healthCheckCache'

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>()

  get length(): number { return this.values.size }
  clear(): void { this.values.clear() }
  getItem(key: string): string | null { return this.values.get(key) ?? null }
  key(index: number): string | null { return Array.from(this.values.keys())[index] ?? null }
  removeItem(key: string): void { this.values.delete(key) }
  setItem(key: string, value: string): void { this.values.set(key, value) }
}

beforeEach(() => {
  vi.stubGlobal('localStorage', new MemoryStorage())
})

const sampleCache: HealthCheckCache = {
  ranAt: '2026-08-13T06:30:00.000Z',
  checks: [
    { key: 'engine', group: 'services', name: 'Engine 服务', detail: 'http://localhost:5208 · 总耗时 8 ms', status: 'ok', latencyMs: 8 },
    { key: 'database', group: 'infrastructure', name: 'PostgreSQL', detail: 'Database is reachable', status: 'ok', latencyMs: 4 },
  ],
}

describe('health check cache', () => {
  it('round-trips the last run through storage', () => {
    saveHealthCheckCache(localStorage, sampleCache)

    expect(loadHealthCheckCache(localStorage)).toEqual(sampleCache)
  })

  it('returns null when nothing has been cached', () => {
    expect(loadHealthCheckCache(localStorage)).toBeNull()
  })

  it('returns null for corrupted JSON', () => {
    localStorage.setItem('openagent.health-check.cache', '{not json')

    expect(loadHealthCheckCache(localStorage)).toBeNull()
  })

  it('returns null for an unexpected shape', () => {
    localStorage.setItem('openagent.health-check.cache', JSON.stringify({ ranAt: 42, checks: 'nope' }))

    expect(loadHealthCheckCache(localStorage)).toBeNull()
  })

  it('aligns cached checks with the current seed items', () => {
    const seed: CheckItem[] = [
      { key: 'engine', group: 'services', name: 'Engine 服务', detail: '待检测', status: 'idle' },
      { key: 'router', group: 'services', name: 'Router 服务', detail: '待检测', status: 'idle' },
      { key: 'database', group: 'infrastructure', name: 'PostgreSQL', detail: '待检测', status: 'idle' },
    ]
    const cached: CheckItem[] = [
      { key: 'engine', group: 'services', name: 'Engine 服务', detail: 'http://localhost:5208 · 总耗时 8 ms', status: 'ok', latencyMs: 8 },
      { key: 'legacy', group: 'services', name: 'Legacy', detail: 'gone', status: 'error' },
    ]

    const merged = mergeChecksWithSeed(seed, cached)

    expect(merged).toEqual([
      { key: 'engine', group: 'services', name: 'Engine 服务', detail: 'http://localhost:5208 · 总耗时 8 ms', status: 'ok', latencyMs: 8 },
      { key: 'router', group: 'services', name: 'Router 服务', detail: '待检测', status: 'idle' },
      { key: 'database', group: 'infrastructure', name: 'PostgreSQL', detail: '待检测', status: 'idle' },
    ])
    expect(merged.some(item => item.key === 'legacy')).toBe(false)
  })
})
