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
    strategy: summary.strategy === 'summarization' ? '摘要' : summary.strategy,
    trigger: summary.trigger === 'Manual' ? '手动' : '自动',
    status: summary.status === 'Succeeded' ? '已完成' : summary.status === 'Skipped' ? '未执行' : '失败',
    tagType: summary.status === 'Succeeded' ? 'success' : summary.status === 'Skipped' ? 'warning' : 'danger',
    detail: summary.summary || summary.result || summary.error || '暂无压缩结果',
    recovered: summary.originalHistoryRestored,
  }
}
