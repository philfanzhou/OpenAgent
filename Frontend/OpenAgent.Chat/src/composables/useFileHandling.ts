import { ref } from 'vue'
import { api } from '../api'
import type { ConversationRecord, MessageFile, PendingFile } from '../types'

interface FileHandlingOptions {
  getSelectedConversation: () => ConversationRecord | null
  notifyError: (error: unknown) => void
}

export function isTextPreview(mediaType: string): boolean {
  return mediaType.startsWith('text/') || mediaType === 'application/json'
}

export async function toMessageFile(item: PendingFile): Promise<MessageFile> {
  const asset = item.asset
  if (!asset) throw new Error('文件尚未上传完成')
  return {
    fileId: asset.fileId,
    fileName: asset.fileName,
    mediaType: asset.mediaType,
    length: asset.length,
    previewUrl: asset.mediaType.startsWith('image/') ? URL.createObjectURL(item.file) : undefined,
    previewText: isTextPreview(asset.mediaType) ? await item.file.text() : undefined,
  }
}

export function useFileHandling(options: FileHandlingOptions) {
  const pendingFiles = ref<PendingFile[]>([])

  function handleFilesChange(files: PendingFile[]): void {
    const existing = new Map(pendingFiles.value.map(item => [item.id, item]))
    pendingFiles.value = files.map(item => existing.get(item.id) || item)
    for (const item of pendingFiles.value.filter(item => item.state === 'uploading' && !existing.has(item.id))) {
      void uploadPendingFile(item.id)
    }
  }

  function retryPendingFile(id: string): void {
    const file = pendingFiles.value.find(item => item.id === id)
    if (!file || file.state === 'uploading') return
    file.state = 'uploading'
    file.error = undefined
    void uploadPendingFile(id)
  }

  async function uploadPendingFile(id: string): Promise<void> {
    const pending = pendingFiles.value.find(item => item.id === id)
    if (!pending) return
    try {
      const asset = await api.uploadFile(pending.file)
      const current = pendingFiles.value.find(item => item.id === id)
      if (!current) return
      current.asset = asset
      current.state = 'ready'
    } catch (error) {
      const current = pendingFiles.value.find(item => item.id === id)
      if (!current) return
      current.state = 'failed'
      current.error = error instanceof Error ? error.message : '上传失败'
      options.notifyError(error)
    }
  }

  async function hydrateFilePreviews(conversation: ConversationRecord): Promise<void> {
    const files = conversation.messages?.flatMap(item => item.files || []) || []
    await Promise.all(files.map(async file => {
      if (!file.fileId || file.previewUrl || file.previewText) return
      try {
        if (file.mediaType.startsWith('image/')) {
          file.previewUrl = await api.loadFilePreview(file.fileId, conversation.conversationId)
        } else if (isTextPreview(file.mediaType)) {
          file.previewText = await api.readFileText(file.fileId, conversation.conversationId)
        }
      } catch {
        // The file remains downloadable even when preview loading fails.
      }
    }))
  }

  async function downloadFile(file: MessageFile): Promise<void> {
    const conversation = options.getSelectedConversation()
    if (!file.fileId || !conversation) return
    try {
      await api.downloadFile(file.fileId, file.fileName, conversation.conversationId)
    } catch (error) {
      options.notifyError(error)
    }
  }

  function clearPendingFiles(): void {
    pendingFiles.value = []
  }

  return {
    pendingFiles,
    handleFilesChange,
    retryPendingFile,
    hydrateFilePreviews,
    downloadFile,
    clearPendingFiles,
  }
}
