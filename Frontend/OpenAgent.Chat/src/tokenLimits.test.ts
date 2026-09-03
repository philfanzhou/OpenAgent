import { describe, expect, it } from 'vitest'
import { validateTokenLimits } from './tokenLimits'

describe('token limit editing', () => {
  it.each([
    [null, null], [128000, null], [undefined, 4096], [128000, 4096],
  ])('allows optional defaults and valid budgets (%s, %s)', (context, output) => {
    expect(validateTokenLimits(context, output)).toBeNull()
  })

  it.each([
    [0, null], [NaN, 1], [Infinity, 1], [128000, -1], [128000, 0.5],
    [128000, 128000], [128000, 128001], [2147483648, 1],
  ])('rejects invalid or exhausted budgets (%s, %s)', (context, output) => {
    expect(validateTokenLimits(context, output)).not.toBeNull()
  })

  it('requires a model context while allowing an empty Agent default', () => {
    expect(validateTokenLimits(null, null, true)).not.toBeNull()
    expect(validateTokenLimits(null, null)).toBeNull()
  })
})
