function randomSource(): Crypto {
  const value = globalThis.crypto
  if (!value || typeof value.getRandomValues !== 'function') {
    throw new Error('当前页面不支持安全随机数，请使用 HTTPS 或更新浏览器后重试。')
  }
  return value
}

export function randomUuid(): string {
  const value = randomSource()
  if (typeof value.randomUUID === 'function') return value.randomUUID()

  const bytes = new Uint8Array(16)
  value.getRandomValues(bytes)
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export function randomUrlSafe(bytes: number): string {
  const value = new Uint8Array(bytes)
  randomSource().getRandomValues(value)
  return toBase64Url(value)
}

export async function sha256(value: string): Promise<string> {
  const cryptoApi = randomSource()
  if (!cryptoApi.subtle) {
    throw new Error('OIDC PKCE 需要安全上下文，请通过 HTTPS 访问前端。')
  }
  const digest = await cryptoApi.subtle.digest('SHA-256', new TextEncoder().encode(value))
  return toBase64Url(new Uint8Array(digest))
}

function toBase64Url(value: Uint8Array): string {
  let binary = ''
  for (const byte of value) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}
