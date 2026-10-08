export function parseSkillMarkdown(markdown: string): { name: string; description: string; body: string } | null {
  const lines = markdown.replace(/^\uFEFF/, '').split(/\r?\n/)
  if (lines[0]?.trim() !== '---') return null
  const end = lines.findIndex((line, index) => index > 0 && line.trim() === '---')
  if (end < 0) return null
  const values = new Map<string, string>()
  for (const line of lines.slice(1, end)) {
    const separator = line.indexOf(':')
    if (separator > 0) values.set(line.slice(0, separator).trim().toLowerCase(), line.slice(separator + 1).trim().replace(/^['"]|['"]$/g, ''))
  }
  const name = values.get('name')?.trim() || ''
  const description = values.get('description')?.trim() || ''
  return name && description ? { name, description, body: lines.slice(end + 1).join('\n').replace(/^\n/, '') } : null
}

export function composeSkillMarkdown(name: string, description: string, instructions: string): string {
  return `---\nname: ${name.trim()}\ndescription: ${description.trim()}\n---\n\n${instructions}`
}
