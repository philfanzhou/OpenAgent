import { jsonInit, request, uploadForm } from '../../shared/api/http'
import type { AgentConfigEntity, SkillCatalogItem, SkillInstanceConfig, SkillPackageInstallResponse, SkillTestResult } from '../../shared/contracts/types'

export const api = {
  uploadSkillPackage(agentId: string, file: File): Promise<SkillPackageInstallResponse> {
    return uploadForm<SkillPackageInstallResponse>(`/api/v1/admin/skills/${encodeURIComponent(agentId)}/packages`, file)
  },

  uploadSkillCatalog(file: File): Promise<{ skill: SkillInstanceConfig; storage: string }> {
    return uploadForm<{ skill: SkillInstanceConfig; storage: string }>('/api/v1/admin/skills/packages', file)
  },

  deleteSkillCatalog(skillId: string): Promise<void> {
    return request<void>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}`, { method: 'DELETE' })
  },

  updateSkillScriptExecution(skillId: string, scriptExecutionEnabled: boolean): Promise<SkillCatalogItem> {
    return request<SkillCatalogItem>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}`, jsonInit('PATCH', { scriptExecutionEnabled }))
  },

  listSkills(): Promise<SkillCatalogItem[]> {
    return request<SkillCatalogItem[]>('/api/v1/admin/skills')
  },

  getSkill(skillId: string): Promise<SkillCatalogItem> {
    return request<SkillCatalogItem>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}`)
  },

  getSkillSource(skillId: string): Promise<{ markdown: string }> {
    return request<{ markdown: string }>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}/source`)
  },

  updateSkillSource(skillId: string, markdown: string): Promise<SkillCatalogItem> {
    return request<SkillCatalogItem>(`/api/v1/admin/skills/${encodeURIComponent(skillId)}/source`, jsonInit('PUT', { markdown }))
  },

  deleteSkillPackage(agentId: string, skillId: string): Promise<void> {
    return request<void>(`/api/v1/admin/skills/${encodeURIComponent(agentId)}/${encodeURIComponent(skillId)}`, { method: 'DELETE' })
  },

  testSkills(skills: AgentConfigEntity['config']['skills']): Promise<SkillTestResult> {
    return request<SkillTestResult>('/api/v1/admin/skills/test', jsonInit('POST', skills))
  }
}
