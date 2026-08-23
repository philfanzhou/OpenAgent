import { describe, expect, it } from 'vitest'
import { createStreamingAssistantContentState, enqueueAssistantContent, markAssistantPhaseBoundary } from './streamingAssistantContent'

describe('streaming assistant content', () => {
  it('inserts the phase line break while the next phase is still streaming', () => {
    const output: string[] = []
    const state = createStreamingAssistantContentState()

    enqueueAssistantContent(state, content => output.push(content), 'first')
    markAssistantPhaseBoundary(state)
    enqueueAssistantContent(state, content => output.push(content), 'second')

    expect(output.join('')).toBe('first\nsecond')
  })

  it('does not duplicate an existing line break', () => {
    const output: string[] = []
    const state = createStreamingAssistantContentState()

    enqueueAssistantContent(state, content => output.push(content), 'first\n')
    markAssistantPhaseBoundary(state)
    enqueueAssistantContent(state, content => output.push(content), '\nsecond')

    expect(output.join('')).toBe('first\n\nsecond')
  })
})
