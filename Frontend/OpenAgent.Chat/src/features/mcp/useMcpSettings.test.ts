import { describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import type { McpConnectionTestResult } from '../../shared/contracts/types'
import { api } from './api'
import { useMcpSettings } from './useMcpSettings'

vi.mock('./api', () => ({ api: { testConnection: vi.fn(), listMcpProfiles: vi.fn(), saveMcpProfile: vi.fn(), deleteMcpProfile: vi.fn() } }))
vi.mock('element-plus', () => ({ ElMessage: { success: vi.fn() }, ElMessageBox: { confirm: vi.fn() } }))

const makeResult = (name: string, latencyMs = 12): McpConnectionTestResult => ({ success: true, connected: true, latencyMs, serverName: name, serverVersion: '1', protocolVersion: '2026-07-28', tools: [], error: null })
const options = { agentId: () => undefined, notifyError: vi.fn() }

function deferredResult() {
  let resolve!: (value: McpConnectionTestResult) => void
  const promise = new Promise<McpConnectionTestResult>(complete => { resolve = complete })
  return { promise, resolve }
}

describe('useMcpSettings connection diagnostics', () => {
  it('keeps a snapshot of the tested draft', async () => {
    const state = useMcpSettings(options)
    state.mcpDraft.value = { name: 'draft', url: ' https://example.test ', type: 'Http' }
    vi.mocked(api.testConnection).mockResolvedValueOnce(makeResult('draft'))
    const pending = state.testMcp(state.mcpDraft.value)
    state.mcpDraft.value.url = 'changed'
    await pending
    expect(state.mcpTestTarget.value).toEqual({ name: 'draft', url: 'https://example.test', type: 'Http' })
  })

  it('applies only the latest repeated test response', async () => {
    const state = useMcpSettings(options)
    const first = deferredResult()
    vi.mocked(api.testConnection).mockReturnValueOnce(first.promise).mockResolvedValueOnce(makeResult('latest', 20))
    const firstPending = state.testMcp({ name: 'one', url: 'https://one', type: 'Http' })
    await state.testMcp({ name: 'two', url: 'https://two', type: 'Http' })
    first.resolve(makeResult('stale'))
    await firstPending
    expect(state.mcpTestResult.value?.serverName).toBe('latest')
  })

  it('clears pending results and ignores a late response', async () => {
    const state = useMcpSettings(options)
    const pending = deferredResult()
    vi.mocked(api.testConnection).mockReturnValueOnce(pending.promise)
    const request = state.testMcp({ name: 'one', url: 'https://one', type: 'Http' })
    state.clearMcpTest()
    pending.resolve(makeResult('late'))
    await request
    await nextTick()
    expect(state.mcpTestTarget.value).toBeNull()
    expect(state.mcpTestResult.value).toBeNull()
    expect(state.testingMcp.value).toBe(false)
  })

  it('stores a safe failure result when the API rejects', async () => {
    const state = useMcpSettings(options)
    vi.mocked(api.testConnection).mockRejectedValueOnce(new Error('请求失败'))
    await state.testMcp({ name: 'one', url: 'https://one', type: 'Http' })
    expect(state.mcpTestResult.value).toMatchObject({ success: false, connected: false, tools: [], error: '请求失败' })
  })
})
