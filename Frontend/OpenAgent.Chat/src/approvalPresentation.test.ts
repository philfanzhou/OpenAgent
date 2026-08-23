import { describe, expect, it } from 'vitest'
import { buildApprovalNoticePresentation, formatApprovalTargetType } from './approvalPresentation'

describe('approval notice presentation', () => {
  it('keeps a pending approval visible while the conversation is paused', () => {
    expect(buildApprovalNoticePresentation('Pending', 'AwaitingApproval')).toMatchObject({
      state: 'pending',
      title: '等待人工审批',
    })
  })

  it('uses the terminal conversation when persisted approval metadata is stale', () => {
    expect(buildApprovalNoticePresentation('Pending', 1)).toMatchObject({
      state: 'approved',
      title: '审批已通过',
    })
  })

  it('prefers an explicit terminal approval status', () => {
    expect(buildApprovalNoticePresentation(2, 'Completed')).toMatchObject({
      state: 'rejected',
      title: '审批已拒绝',
    })
  })

  it('formats numeric resource types returned by the API', () => {
    expect(formatApprovalTargetType(5)).toBe('Skill')
    expect(formatApprovalTargetType('Mcp')).toBe('Mcp')
  })
})
