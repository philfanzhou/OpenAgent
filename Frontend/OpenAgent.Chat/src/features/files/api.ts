import { fetchObjectUrl, send, uploadForm } from '../../shared/api/http'
import type { FileAsset } from '../../shared/contracts/types'

export const api = {
  uploadFile(file: File): Promise<FileAsset> {
    return uploadForm<FileAsset>('/api/v1/agent/files', file)
  },

  loadFilePreview(fileId: string, conversationId: string): Promise<string> {
    return fetchObjectUrl(
      `/api/v1/agent/files/${encodeURIComponent(fileId)}/content?conversationId=${encodeURIComponent(conversationId)}`,
    )
  },

  async loadObjectPreview(objectKey: string, conversationId?: string): Promise<string> {
    const query = new URLSearchParams({ path: objectKey })
    if (conversationId) query.set('conversationId', conversationId)
    return fetchObjectUrl(`/api/v1/agent/files/object?${query.toString()}`)
  },

  async readFileText(fileId: string, conversationId: string): Promise<string> {
    const response = await send(
      `/api/v1/agent/files/${encodeURIComponent(fileId)}/content?conversationId=${encodeURIComponent(conversationId)}`,
    )
    return await response.text()
  },

  async downloadFile(fileId: string, fileName: string, conversationId: string): Promise<void> {
    const response = await send(
      `/api/v1/agent/files/${encodeURIComponent(fileId)}/download?conversationId=${encodeURIComponent(conversationId)}`,
    )
    const url = URL.createObjectURL(await response.blob())
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = fileName
    anchor.click()
    URL.revokeObjectURL(url)
  }
}
