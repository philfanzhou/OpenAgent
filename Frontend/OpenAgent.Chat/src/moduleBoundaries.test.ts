import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const moduleSources = import.meta.glob<string>(
  ['./features/**/*.ts', './features/**/*.vue', './shared/**/*.ts'],
  { query: '?raw', eager: true, import: 'default' },
)

function importsIn(source: string): string[] {
  const script = source.includes('<script')
    ? [...source.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)].map(match => match[1]).join('\n')
    : source
  const file = ts.createSourceFile('module.ts', script, ts.ScriptTarget.Latest, true)
  const imports: string[] = []
  function visit(node: ts.Node): void {
    if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node))
      && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
      imports.push(node.moduleSpecifier.text)
    }
    if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword
      && node.arguments[0] && ts.isStringLiteral(node.arguments[0])) {
      imports.push(node.arguments[0].text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return imports
}

function owner(path: string): string {
  const relative = path.replace(/^\.\/|^\/src\//, '')
  if (relative.startsWith('features/')) return relative.split('/')[1]!
  return relative.startsWith('shared/') ? 'shared' : 'app'
}

function violates(from: string, to: string): boolean {
  return from === 'shared' ? to !== 'shared' : from !== 'app' && to !== 'shared' && from !== to
}

describe('module boundaries', () => {
  it('keeps shared independent and feature implementations within their own domain', () => {
    const violations: string[] = []
    for (const [file, source] of Object.entries(moduleSources)) {
      for (const reference of importsIn(source).filter(item => item.startsWith('.'))) {
        const target = new URL(reference, `https://modules.local/src/${file.slice(2)}`).pathname
        if (violates(owner(file), owner(target))) violations.push(`${file} -> ${reference}`)
      }
    }
    expect(violations).toEqual([])
  })

  it('checks re-exports and dynamic imports as well as normal imports', () => {
    expect(importsIn("export * from '../skills/api'; import('../app/entry'); import { api } from './api'"))
      .toEqual(['../skills/api', '../app/entry', './api'])
    expect(violates('mcp', 'skills')).toBe(true)
    expect(violates('shared', 'app')).toBe(true)
    expect(violates('mcp', 'shared')).toBe(false)
  })
})
