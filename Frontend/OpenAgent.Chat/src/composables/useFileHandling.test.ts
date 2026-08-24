import { describe, expect, it, vi } from 'vitest'
import { isTextPreview, toMessageFile } from './useFileHandling'
import type { PendingFile } from '../types'

function readyFile(mediaType: string, content = 'hello'): PendingFile {
  const file = new File([content], 'sample.txt', { type: mediaType })
  return {
    id: 'pending-1',
    file,
    state: 'ready',
    asset: {
      fileId: 'file-1',
      tenantId: 'tenant-1',
      ownerUserId: 'user-1',
      fileName: file.name,
      mediaType,
      length: file.size,
      sha256: 'hash',
      source: 'UserUpload',
      state: 'Ready',
      createdAt: '2026-08-20T00:00:00.000Z',
    },
  }
}

describe('file handling', () => {
  it.each([
    ['text/plain', true],
    ['application/json', true],
    ['application/pdf', false],
    ['image/png', false],
  ])('classifies %s preview support', (mediaType, expected) => {
    expect(isTextPreview(mediaType)).toBe(expected)
  })

  it('preserves uploaded asset metadata and text preview', async () => {
    const result = await toMessageFile(readyFile('text/plain'))

    expect(result).toMatchObject({
      fileId: 'file-1',
      fileName: 'sample.txt',
      mediaType: 'text/plain',
      previewText: 'hello',
    })
  })

  it('creates an object URL for image previews', async () => {
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:preview')

    const result = await toMessageFile(readyFile('image/png'))

    expect(result.previewUrl).toBe('blob:preview')
    expect(createObjectURL).toHaveBeenCalledOnce()
  })

  it('rejects files that have not completed uploading', async () => {
    const pending = readyFile('text/plain')
    pending.asset = undefined

    await expect(toMessageFile(pending)).rejects.toThrow('文件尚未上传完成')
  })
})
