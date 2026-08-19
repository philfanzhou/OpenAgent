import type { ContextSummary } from './types'

export interface CompactionDisplay {
  strategy: string
  trigger: string
  status: string
  detail: string
  recovered: boolean
}

export function buildCompactionDisplay(summary: ContextSummary): CompactionDisplay {
  const strategies: Record<string, string> = {
    truncation: '截断',
    sliding_window: '滑动窗口',
    summarization: '摘要',
  }
  return {
    strategy: strategies[summary.strategy] || summary.strategy,
    trigger: summary.trigger === 'Manual' ? '手动' : '自动',
    status: summary.status === 'Succeeded' ? '已完成' : '失败',
    detail: summary.summary || summary.result || summary.error || '暂无压缩结果',
    recovered: summary.originalHistoryRestored,
  }
}
