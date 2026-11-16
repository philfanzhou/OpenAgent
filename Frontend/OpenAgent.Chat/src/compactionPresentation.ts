import type { ContextSummary } from './types'

export interface CompactionDisplay {
  strategy: string
  trigger: string
  status: string
  tagType: 'success' | 'warning' | 'danger'
  detail: string
  recovered: boolean
}

export interface CompactionTokenDisplay {
  before: number | null
  after: number
  retainedPercent: string
}

export function buildCompactionTokenDisplay(summary: ContextSummary): CompactionTokenDisplay {
  const before = summary.originalTokenCount > 0 ? summary.originalTokenCount : null
  const after = Math.max(summary.tokenCount || 0, 0)
  return {
    before,
    after,
    retainedPercent: before != null ? `${Math.round((after / before) * 100)}%` : '—',
  }
}

export function buildCompactionDisplay(summary: ContextSummary): CompactionDisplay {
  return {
    strategy: summary.strategy === 'runtime' ? '运行时压缩' : summary.strategy === 'summarization' ? '摘要' : summary.strategy,
    trigger: summary.trigger === 'Manual' ? '手动' : summary.trigger === 'Runtime' ? '运行时' : '自动',
    status: summary.status === 'Succeeded' ? '已完成' : summary.status === 'Skipped' ? '未执行' : summary.status === 'InProgress' ? '压缩中' : summary.status === 'Cancelled' ? '已取消' : summary.status === 'Interrupted' ? '已中断' : summary.status === 'Failed' ? '失败' : '状态未知',
    tagType: summary.status === 'Succeeded' ? 'success' : summary.status === 'Failed' ? 'danger' : 'warning',
    detail: summary.error || summary.summary || summary.result || (summary.isRuntimeEvent ? '运行时未提供可展示的压缩摘要' : '暂无压缩结果'),
    recovered: summary.originalHistoryRestored,
  }
}
