import { api } from './api'
import type { MessageFile } from './types'

/** 内联图片 ![alt](inner)，inner 支持 url、<url> 及带标题形式。 */
const INLINE_IMAGE_PATTERN = /(!\[[^\]]*\]\()([^)]*)(\))/g
/** 引用式定义行 [id]: src（可选引用块标记）。 */
const REFERENCE_DEF_PATTERN = /^([ \t]*(?:>[ \t]*)?\[[^\]]+\]:[ \t]*)([^\s]+)/gm

/** 从内联图片括号内容中取出纯 src：剥离尖括号与标题。 */
function cleanInlineImageSrc(inner: string): string {
  const trimmed = inner.trim()
  if (trimmed.startsWith('<')) {
    const end = trimmed.indexOf('>')
    return end >= 0 ? trimmed.slice(1, end) : trimmed.slice(1)
  }
  return trimmed.match(/^\S+/)?.[0] ?? ''
}

export function isSelfContainedImageRef(src: string): boolean {
  const candidate = src.trim().toLowerCase()
  return candidate.startsWith('http://')
    || candidate.startsWith('https://')
    || candidate.startsWith('data:')
    || candidate.startsWith('#')
}

/** 收集 markdown 源文中全部图片引用（内联 + 引用式定义），去重保序。 */
export function extractImageRefs(markdown: string): string[] {
  const refs: string[] = []
  for (const match of markdown.matchAll(INLINE_IMAGE_PATTERN)) {
    const src = cleanInlineImageSrc(match[2] ?? '')
    if (src && !refs.includes(src)) refs.push(src)
  }
  for (const match of markdown.matchAll(REFERENCE_DEF_PATTERN)) {
    const src = match[2]
    if (src && !refs.includes(src)) refs.push(src)
  }
  return refs
}

/**
 * 以 baseKey 的父目录为基准解析相对引用，得到对象键。
 * 引用越出键根目录（.. 超出顶层）或为绝对路径时返回 undefined；
 * 租户分区校验由服务端 /files/object 端点最终强制执行。
 */
export function resolveRelativeObjectKey(baseKey: string, ref: string): string | undefined {
  if (!baseKey || !ref || ref.trim().startsWith('/')) return undefined
  const segments = baseKey.split('/').slice(0, -1)
  for (const segment of ref.replaceAll('\\', '/').split('/')) {
    if (!segment || segment === '.') continue
    if (segment === '..') {
      if (!segments.length) return undefined
      segments.pop()
      continue
    }
    segments.push(segment)
  }
  const resolved = segments.join('/')
  return resolved && resolved !== baseKey ? resolved : undefined
}

function basename(path: string): string {
  const normalized = path.replaceAll('\\', '/')
  const last = normalized.lastIndexOf('/')
  return last >= 0 ? normalized.slice(last + 1) : normalized
}

/** blob URL 缓存：同一 fileId/objectKey 在会话内只拉取一次；失败条目移除以便重试。 */
const blobUrlCache = new Map<string, Promise<string>>()

async function cacheBlob(key: string, loader: () => Promise<string>): Promise<string> {
  let cached = blobUrlCache.get(key)
  if (!cached) {
    cached = loader().catch(error => {
      blobUrlCache.delete(key)
      throw error
    })
    blobUrlCache.set(key, cached)
  }
  return cached
}

export interface MarkdownImageResolverContext {
  conversationId?: string
  /** 被预览 markdown 自身的对象键，相对引用以它的父目录为基准解析。 */
  selfObjectKey?: string
  /** 同消息/会话内的候选文件，按文件名匹配引用。 */
  siblingFiles: MessageFile[]
}

export interface MarkdownImageResolver {
  resolve(src: string): Promise<string | undefined>
}

/**
 * 将 markdown 图片引用解析为带鉴权 fetch 得到的 blob URL：
 * 1) http(s)/data/锚点原样保留（返回 undefined 表示不重写）；
 * 2) 文件名匹配会话文件 → /files/{id}/content；
 * 3) 相对路径基于 selfObjectKey 解析对象键 → /files/object；
 * 4) 均失败返回 undefined，渲染保留原引用。
 */
export function createImageResolver(context: MarkdownImageResolverContext): MarkdownImageResolver {
  const byName = new Map<string, MessageFile>()
  for (const file of context.siblingFiles) {
    const name = basename(file.fileName).toLowerCase()
    if (name && !byName.has(name)) byName.set(name, file)
  }

  async function resolve(src: string): Promise<string | undefined> {
    if (!src || isSelfContainedImageRef(src)) return undefined

    const sibling = byName.get(basename(src).toLowerCase())
    if (sibling?.fileId && context.conversationId) {
      return cacheBlob(`file:${sibling.fileId}`, () =>
        api.loadFilePreview(sibling.fileId!, context.conversationId!))
    }

    if (context.selfObjectKey) {
      const objectKey = resolveRelativeObjectKey(context.selfObjectKey, src)
      if (objectKey) {
        return cacheBlob(`obj:${objectKey}`, () =>
          api.loadObjectPreview(objectKey, context.conversationId))
      }
    }
    return undefined
  }

  return { resolve }
}

/** 用 lookup 命中的 URL 重写 markdown 中命中的图片引用，未命中保持原样。 */
export function rewriteMarkdownImages(
  markdown: string,
  lookup: (src: string) => string | undefined,
): string {
  let result = markdown.replace(INLINE_IMAGE_PATTERN, (whole, lead: string, inner: string, tail: string) => {
    const replacement = lookup(cleanInlineImageSrc(inner))
    return replacement ? `${lead}${replacement}${tail}` : whole
  })
  result = result.replace(REFERENCE_DEF_PATTERN, (whole, lead: string, src: string) => {
    const replacement = lookup(src)
    return replacement ? `${lead}${replacement}` : whole
  })
  return result
}

/** 解析并整体重写：先并发解析所有引用，再把命中的替换为 blob URL 后返回新源文。 */
export async function resolveMarkdownImages(
  markdown: string,
  resolver: MarkdownImageResolver,
): Promise<string> {
  const refs = extractImageRefs(markdown).filter(ref => !isSelfContainedImageRef(ref))
  if (!refs.length) return markdown
  const resolved = await Promise.all(refs.map(ref => resolver.resolve(ref).catch(() => undefined)))
  const urlByRef = new Map<string, string>()
  refs.forEach((ref, index) => {
    const url = resolved[index]
    if (url) urlByRef.set(ref, url)
  })
  if (!urlByRef.size) return markdown
  return rewriteMarkdownImages(markdown, src => urlByRef.get(src))
}
