import { describe, expect, it } from 'vitest'
import { api } from './api'
import {
  createImageResolver,
  extractImageRefs,
  isSelfContainedImageRef,
  resolveRelativeObjectKey,
  rewriteMarkdownImages,
} from './markdownAssets'
import type { MessageFile } from './types'

const baseKey = 'files/tenants/t1/users/u1/report.md'

describe('extractImageRefs', () => {
  it('collects inline and reference-style image refs without duplicates', () => {
    const markdown = [
      '![chart](./images/a.png)',
      '![remote](https://example.com/b.png)',
      '![ref][pic]',
      '',
      '[pic]: ./images/c.png',
    ].join('\n')

    expect(extractImageRefs(markdown)).toEqual(['./images/a.png', 'https://example.com/b.png', './images/c.png'])
  })

  it('handles titles and angle-bracket refs', () => {
    const markdown = '![alt](<./imgs/x y.png> "title")'

    expect(extractImageRefs(markdown)).toEqual(['./imgs/x y.png'])
  })
})

describe('isSelfContainedImageRef', () => {
  it('treats http(s), data and anchors as self-contained', () => {
    expect(isSelfContainedImageRef('https://example.com/a.png')).toBe(true)
    expect(isSelfContainedImageRef('HTTP://example.com/a.png')).toBe(true)
    expect(isSelfContainedImageRef('data:image/png;base64,AAAA')).toBe(true)
    expect(isSelfContainedImageRef('#anchor')).toBe(true)
    expect(isSelfContainedImageRef('./a.png')).toBe(false)
    expect(isSelfContainedImageRef('a.png')).toBe(false)
  })
})

describe('resolveRelativeObjectKey', () => {
  it('resolves sibling and nested refs against the base key directory', () => {
    expect(resolveRelativeObjectKey(baseKey, './images/a.png'))
      .toBe('files/tenants/t1/users/u1/images/a.png')
    expect(resolveRelativeObjectKey(baseKey, 'a.png'))
      .toBe('files/tenants/t1/users/u1/a.png')
  })

  it('supports parent traversal but rejects escaping the key root', () => {
    const deep = 'files/tenants/t1/users/u1/docs/report.md'
    expect(resolveRelativeObjectKey(deep, '../images/a.png'))
      .toBe('files/tenants/t1/users/u1/images/a.png')
    // 越出键根目录由客户端拒绝；仍在根内的可疑路径交给服务端租户分区校验拦截。
    expect(resolveRelativeObjectKey(baseKey, '../../../../etc/passwd'))
      .toBe('files/etc/passwd')
    expect(resolveRelativeObjectKey(baseKey, '../../../../../../etc/passwd')).toBeUndefined()
    expect(resolveRelativeObjectKey(baseKey, '/abs/a.png')).toBeUndefined()
  })
})

describe('rewriteMarkdownImages', () => {
  it('replaces matched refs inline and in reference definitions, keeping others', () => {
    const markdown = '![a](./a.png) ![keep](./b.png)\n\n[def]: ./c.png'
    const result = rewriteMarkdownImages(markdown, src =>
      src === './a.png' ? 'blob:one' : src === './c.png' ? 'blob:three' : undefined)

    expect(result).toContain('![a](blob:one)')
    expect(result).toContain('![keep](./b.png)')
    expect(result).toContain('[def]: blob:three')
  })
})

describe('createImageResolver', () => {
  const files: MessageFile[] = [
    { fileId: 'file-1', fileName: 'chart.png', mediaType: 'image/png', length: 1 },
  ]

  it('prefers sibling file match by basename', async () => {
    let requestedUrl = ''
    const loadFilePreview = async (fileId: string) => {
      requestedUrl = fileId
      return `blob:${fileId}`
    }
    const resolver = withApi({ loadFilePreview }, () => createImageResolver({
      conversationId: 'c1',
      selfObjectKey: baseKey,
      siblingFiles: files,
    }).resolve('./sub/chart.png'))

    await expect(resolver).resolves.toBe('blob:file-1')
    expect(requestedUrl).toBe('file-1')
  })

  it('falls back to object-key relative resolution', async () => {
    let requestedPath = ''
    const loadObjectPreview = async (path: string) => {
      requestedPath = path
      return `blob:${path}`
    }
    const resolver = withApi({ loadObjectPreview }, () => createImageResolver({
      conversationId: 'c1',
      selfObjectKey: baseKey,
      siblingFiles: [],
    }).resolve('./images/a.png'))

    await expect(resolver).resolves.toBe('blob:files/tenants/t1/users/u1/images/a.png')
    expect(requestedPath).toBe('files/tenants/t1/users/u1/images/a.png')
  })
})

/** 在解析期间临时替换 api 方法，避免测试发起真实请求。 */
function withApi<T>(overrides: Record<string, unknown>, action: () => Promise<T>): Promise<T> {
  const originals = new Map<string, unknown>()
  for (const [key, value] of Object.entries(overrides)) {
    originals.set(key, (api as unknown as Record<string, unknown>)[key])
    ;(api as unknown as Record<string, unknown>)[key] = value
  }
  return action().finally(() => {
    for (const [key, value] of originals) {
      ;(api as unknown as Record<string, unknown>)[key] = value
    }
  })
}
