import MarkdownIt from 'markdown-it'

/** 链接协议白名单：仅允许 http/https/mailto/blob 与相对/锚点链接，拒绝 javascript:/data:/vbscript: 等危险协议。
 *  blob: 为本页面为已鉴权拉取的图片/文件创建的同源对象 URL，markdown 图片渲染依赖它。 */
function isSafeLink(url: string): boolean {
  const candidate = url.trim().toLowerCase()
  if (candidate.startsWith('#') || candidate.startsWith('/')) return true
  const protocol = candidate.match(/^([a-z][a-z0-9+.-]*):/)?.[1]
  if (!protocol) return true
  return protocol === 'http' || protocol === 'https' || protocol === 'mailto' || protocol === 'blob'
}

const renderer = new MarkdownIt({
  breaks: true,
  html: false,
  linkify: true,
  typographer: false,
})
// markdown-it 将 validateLink 暴露为实例方法（@types 未声明），此处显式收紧协议白名单。
;(renderer as unknown as { validateLink: (url: string) => boolean }).validateLink = isSafeLink

const defaultLinkOpen = renderer.renderer.rules.link_open
const defaultAutolink = renderer.renderer.rules.autolink
const defaultFence = renderer.renderer.rules.fence

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, character => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  })[character]!)
}

renderer.renderer.rules.fence = (tokens, index, options, environment, self) => {
  const token = tokens[index]
  if (!token) return ''
  const language = token.info.trim().split(/\s+/, 1)[0] || 'code'
  const codeHtml = defaultFence
    ? defaultFence(tokens, index, options, environment, self)
    : self.renderToken(tokens, index, options)
  return `<div class="markdown-code-block" data-code-block>`
    + `<div class="markdown-code-toolbar">`
    + `<span class="markdown-code-language">${escapeHtml(language)}</span>`
    + `<div class="markdown-code-actions">`
    + `<button type="button" class="markdown-code-action" data-code-action="wrap" aria-pressed="false">自动换行</button>`
    + `<button type="button" class="markdown-code-action" data-code-action="copy">复制</button>`
    + `</div></div>${codeHtml}</div>`
}

// 渲染层防御：即使 validateLink 被绕过/移除，也绝不把危险协议渲染成 href。
renderer.renderer.rules.link_open = (tokens, index, options, environment, self) => {
  const token = tokens[index]
  if (!token) return ''
  const href = token.attrGet('href') ?? ''
  if (!isSafeLink(href)) return ''
  token.attrSet('target', '_blank')
  token.attrSet('rel', 'noopener noreferrer')
  return defaultLinkOpen
    ? defaultLinkOpen(tokens, index, options, environment, self)
    : self.renderToken(tokens, index, options)
}

renderer.renderer.rules.autolink = (tokens, index, options, environment, self) => {
  const token = tokens[index]
  if (!token) return ''
  const href = token.attrGet('href') ?? ''
  if (!isSafeLink(href)) return ''
  return defaultAutolink
    ? defaultAutolink(tokens, index, options, environment, self)
    : self.renderToken(tokens, index, options)
}

export function renderMarkdown(content: string): string {
  return renderer.render(content)
}
