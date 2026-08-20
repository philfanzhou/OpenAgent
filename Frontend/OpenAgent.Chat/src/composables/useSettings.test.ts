import { describe, expect, it } from 'vitest'
import { composeSkillMarkdown, parseSkillMarkdown } from './useSettings'

describe('skill settings markdown', () => {
  it('round-trips the form fields without changing the body', () => {
    const markdown = composeSkillMarkdown('  my-skill ', ' A useful skill ', '# Instructions\n\nKeep this body.\n')

    expect(parseSkillMarkdown(markdown)).toEqual({
      name: 'my-skill',
      description: 'A useful skill',
      body: '# Instructions\n\nKeep this body.\n',
    })
  })

  it('accepts quoted frontmatter values', () => {
    expect(parseSkillMarkdown('---\nname: "quoted"\ndescription: \'description\'\n---\n\nBody')).toEqual({
      name: 'quoted',
      description: 'description',
      body: 'Body',
    })
  })

  it.each([
    ['missing frontmatter', 'name: skill\ndescription: no delimiters'],
    ['missing description', '---\nname: skill\n---\n'],
    ['unterminated frontmatter', '---\nname: skill\ndescription: test'],
  ])('rejects %s', (_scenario, markdown) => {
    expect(parseSkillMarkdown(markdown)).toBeNull()
  })
})
