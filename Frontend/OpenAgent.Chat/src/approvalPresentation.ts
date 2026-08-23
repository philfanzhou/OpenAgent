import type { ConversationStatus } from './types'

export interface ApprovalNoticePresentation {
  state: 'pending' | 'approved' | 'rejected' | 'expired' | 'withdrawn' | 'failed' | 'ended'
  icon: string
  title: string
  detail: string
}

const approvalTargetTypes = ['Agent', 'Model', 'Tool', 'Function', 'MCP', 'Skill'] as const

export function formatApprovalTargetType(value: string | number): string {
  return typeof value === 'number' ? approvalTargetTypes[value] || String(value) : value
}

export function buildApprovalNoticePresentation(
  approvalStatus: string | number | undefined,
  conversationStatus: ConversationStatus | undefined,
): ApprovalNoticePresentation {
  const normalized = typeof approvalStatus === 'string'
    ? approvalStatus.toLowerCase()
    : approvalStatus
  if (normalized === 1 || normalized === 'approved') {
    return notice('approved', '✓', '审批已通过', '已获得审批并完成执行。')
  }
  if (normalized === 2 || normalized === 'rejected') {
    return notice('rejected', '×', '审批已拒绝', '已被审批人拒绝，操作未执行。')
  }
  if (normalized === 3 || normalized === 'expired') {
    return notice('expired', '×', '审批已过期', '因审批超时未执行。')
  }
  if (normalized === 4 || normalized === 'withdrawn') {
    return notice('withdrawn', '×', '审批已撤回', '已由申请人撤回，操作未执行。')
  }

  // Older persisted messages retain ApprovalStatus=Pending. Once the
  // conversation itself reaches a terminal state, it is authoritative for
  // whether the paused tool resumed and prevents a stale "waiting" card.
  if (conversationStatus === 'Completed' || conversationStatus === 1) {
    return notice('approved', '✓', '审批已通过', '已获得审批并完成执行。')
  }
  if (conversationStatus === 'Failed' || conversationStatus === 2) {
    return notice('failed', '×', '审批恢复失败', '审批后的恢复执行失败，请查看错误记录。')
  }
  if (conversationStatus === 'Cancelled' || conversationStatus === 3) {
    return notice('ended', '×', '审批流程已结束', '审批已拒绝、撤回或过期，操作未执行。')
  }
  return notice('pending', '!', '等待人工审批', '已暂停，审批完成前不会执行。')
}

function notice(
  state: ApprovalNoticePresentation['state'],
  icon: string,
  title: string,
  detail: string,
): ApprovalNoticePresentation {
  return { state, icon, title, detail }
}
