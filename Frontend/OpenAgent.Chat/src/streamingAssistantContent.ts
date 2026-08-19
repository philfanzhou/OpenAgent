export interface StreamingAssistantContentState {
  hasContent: boolean
  lastCharacter: string
  needsLineBreak: boolean
}

export function createStreamingAssistantContentState(): StreamingAssistantContentState {
  return { hasContent: false, lastCharacter: '', needsLineBreak: false }
}

export function markAssistantPhaseBoundary(state: StreamingAssistantContentState): void {
  state.needsLineBreak = state.hasContent
}

export function enqueueAssistantContent(
  state: StreamingAssistantContentState,
  enqueue: (content: string) => void,
  content: string,
): void {
  if (!content) return
  if (state.needsLineBreak
    && state.hasContent
    && state.lastCharacter !== '\n'
    && !content.startsWith('\n')) {
    enqueue('\n')
  }
  enqueue(content)
  state.hasContent = true
  state.lastCharacter = [...content].at(-1) || state.lastCharacter
  state.needsLineBreak = false
}
