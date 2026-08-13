import MarkdownIt from 'markdown-it'

/** 链接协议白名单：仅允许 http/https/mailto 与相对/锚点链接，拒绝 javascript:/data:/vbscript: 等危险协议。 */
function isSafeLink(url: string): boolean {
  const candidate = url.trim().toLowerCase()
  if (candidate.startsWith('#') || candidate.startsWith('/')) return true
  const protocol = candidate.match(/^([a-z][a-z0-9+.-]*):/)?.[1]
  if (!protocol) return true
  return protocol === 'http' || protocol === 'https' || protocol === 'mailto'
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

renderer.renderer.rules.link_open = (tokens, index, options, environment, self) => {
  const token = tokens[index]
  if (!token) return ''
  token.attrSet('target', '_blank')
  token.attrSet('rel', 'noopener noreferrer')
  return defaultLinkOpen
    ? defaultLinkOpen(tokens, index, options, environment, self)
    : self.renderToken(tokens, index, options)
}

export function renderMarkdown(content: string): string {
  return renderer.render(content)
}
