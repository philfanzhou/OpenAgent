import { computed, ref, type Ref } from 'vue'

/**
 * Audit console address for the current workspace.
 *
 * Stored per browser rather than per tenant: the console is an operational
 * tool, and an operator switching tenants still looks at the same audit plane.
 * An empty or malformed value falls back to the build-time default so the
 * panel is always actionable.
 */

const storageKey = 'openagent.audit.base-url'

export const defaultAuditBaseUrl: string =
  (import.meta.env.VITE_OPENAGENT_AUDIT_BASE_URL as string | undefined)?.trim()
  || defaultAuditPort()

function defaultAuditPort(): string {
  // Same host as the workspace, audit-web's conventional port. Falls back to
  // localhost when the page is not served over http(s) (unit tests, SSR).
  try {
    const origin = new URL(window.location.origin)
    if (origin.protocol === 'http:' || origin.protocol === 'https:') {
      return `${origin.protocol}//${origin.hostname}:8100`
    }
  } catch {
    /* ignore and use the local default */
  }
  return 'http://localhost:8100'
}

export function normalizeAuditBaseUrl(value: string): string {
  const trimmed = (value || '').trim()
  if (!trimmed) return ''
  try {
    const parsed = new URL(trimmed)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return ''
    // Keep only the origin: the console owns its own paths.
    return parsed.origin
  } catch {
    return ''
  }
}

export function readAuditBaseUrl(): string {
  try {
    return normalizeAuditBaseUrl(localStorage.getItem(storageKey) || '') || defaultAuditBaseUrl
  } catch {
    return defaultAuditBaseUrl
  }
}

export function useAuditSettings() {
  const auditBaseUrl: Ref<string> = ref(readAuditBaseUrl())

  const isValid = computed(() => Boolean(normalizeAuditBaseUrl(auditBaseUrl.value)))

  const saveAuditBaseUrl = (value: string): string => {
    const normalized = normalizeAuditBaseUrl(value)
    const next = normalized || defaultAuditBaseUrl
    auditBaseUrl.value = next
    try {
      if (normalized) localStorage.setItem(storageKey, next)
      else localStorage.removeItem(storageKey)
    } catch {
      /* storage unavailable: keep the in-memory value */
    }
    return next
  }

  const resetAuditBaseUrl = (): string => {
    auditBaseUrl.value = defaultAuditBaseUrl
    try {
      localStorage.removeItem(storageKey)
    } catch {
      /* ignore */
    }
    return defaultAuditBaseUrl
  }

  return { auditBaseUrl, isValid, saveAuditBaseUrl, resetAuditBaseUrl, defaultAuditBaseUrl }
}