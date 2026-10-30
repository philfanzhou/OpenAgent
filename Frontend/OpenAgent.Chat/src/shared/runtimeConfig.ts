export interface RuntimeConfig {
  routerBaseUrl?: string
  engineBaseUrl?: string
  tenantId?: string
}

let runtimeConfig: Readonly<RuntimeConfig> = {}

export function getRuntimeConfig(): Readonly<RuntimeConfig> {
  return runtimeConfig
}

export async function loadRuntimeConfig(): Promise<void> {
  const response = await fetch(`${import.meta.env.BASE_URL}runtime-config.json`, { cache: 'no-store' })
  if (!response.ok) throw new Error(`Runtime configuration request failed: ${response.status}`)
  const value: unknown = await response.json()
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('Invalid runtime configuration')
  }

  const config: RuntimeConfig = {}
  for (const key of ['routerBaseUrl', 'engineBaseUrl', 'tenantId'] as const) {
    const item = (value as Record<string, unknown>)[key]
    if (item === undefined) continue
    if (typeof item !== 'string') throw new Error(`Invalid runtime configuration: ${key}`)
    const trimmed = item.trim()
    if (!trimmed) continue
    if (key !== 'tenantId') {
      const url = new URL(trimmed)
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
        throw new Error(`Invalid runtime configuration: ${key}`)
      }
    }
    config[key] = key === 'tenantId' ? trimmed : trimmed.replace(/\/+$/, '')
  }
  runtimeConfig = Object.freeze(config)
}
