import { ref } from 'vue'
import { api } from '../api'
import {
  createImageResolver,
  extractImageRefs,
  isSelfContainedImageRef,
} from '../markdownAssets'
import type { ConversationRecord, MessageFile, PendingFile } from '../types'

interface FileHandlingOptions {
  getSelectedConversation: () => ConversationRecord | null
  notifyError: (error: unknown) => void
}

export function isTextPreview(mediaType: string): boolean {
  return mediaType.startsWith('text/') || mediaType === 'application/json'
}

export function isMarkdownFile(mediaType: string, fileName?: string): boolean {
  return mediaType === 'text/markdown'
    || (!!fileName && mediaType === 'text/plain' && fileName.toLowerCase().endsWith('.md'))
}

/**
 * 已解析的 markdown 图片 blob URL。键为 `${messageId}|${selfObjectKey ?? ''}|${src}`：
 * messageId 全局唯一可省 conversationId；selfObjectKey 区分同一引用在不同 md 基准下的解析结果。
 */
const markdownImageUrls = ref(new Map<string, string>())

function imageCacheKey(messageId: string, selfObjectKey: string | undefined, src: string): string {
  return `${messageId}|${selfObjectKey ?? ''}|${src}`
}

/** 解析会话内 markdown（消息正文 + md 文件预览）引用的图片并缓存为 blob URL。 */
async function resolveConversationImages(conversation: ConversationRecord): Promise<void> {
  const messages = conversation.messages || []
  const conversationFiles = messages.flatMap(item => item.files || [])
  for (const message of messages) {
    const sources: Array<{ text: string; selfObjectKey?: string }> = [
      ...(message.content ? [{ text: message.content }] : []),
      ...(message.files || [])
        .filter(file => isMarkdownFile(file.mediaType, file.fileName) && file.previewText)
        .map(file => ({ text: file.previewText!, selfObjectKey: file.objectKey })),
    ]
    for (const source of sources) {
      const refs = extractImageRefs(source.text).filter(ref => !isSelfContainedImageRef(ref))
      if (!refs.length) continue
      const resolver = createImageResolver({
        conversationId: conversation.conversationId,
        selfObjectKey: source.selfObjectKey,
        siblingFiles: [...(message.files || []), ...conversationFiles],
      })
      await Promise.all(refs.map(async ref => {
        const key = imageCacheKey(message.messageId, source.selfObjectKey, ref)
        if (markdownImageUrls.value.has(key)) return
        try {
          const url = await resolver.resolve(ref)
          if (url) markdownImageUrls.value.set(key, url)
        } catch {
          // 解析失败保留原引用，文件仍可下载。
        }
      }))
    }
  }
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
    await resolveConversationImages(conversation)
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
    markdownImageUrls,
  }
}
