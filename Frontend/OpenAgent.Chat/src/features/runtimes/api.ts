import { jsonInit, request } from '../../shared/api/http'
import type { RuntimeProfile } from '../../shared/contracts/types'

export const runtimeApi = {
  list: () => request<RuntimeProfile[]>('/api/v1/admin/runtimes'),
  save: (profile: RuntimeProfile) => request<RuntimeProfile>(`/api/v1/admin/runtimes/${encodeURIComponent(profile.id)}`, jsonInit('PUT', profile)),
  remove: (id: string) => request<void>(`/api/v1/admin/runtimes/${encodeURIComponent(id)}`, { method: 'DELETE' }),
}
