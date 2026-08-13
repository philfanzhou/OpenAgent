import MarkdownIt from 'markdown-it'

const renderer = new MarkdownIt({
  breaks: true,
  html: false,
  linkify: true,
  typographer: false,
})

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
