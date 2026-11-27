import { ApiError } from '../../shared/api/http'

/** 排队相关失败给用户看的文案；code 沿用后端错误码名，气泡据此用提示色而不是故障色。 */
export interface QueueNotice {
  title: string
  detail: string
  code: string
}

/** 请求还在队列里、尚未开始执行时，助手气泡显示的占位文案。 */
export const QUEUED_PLACEHOLDER = '正在排队，请稍候…'

/** 队列已满：服务端在开始这一轮之前就以 HTTP 429 拒绝，会话里什么都没有写入。 */
export function describeQueueFull(error: unknown): QueueNotice | undefined {
  if (!(error instanceof ApiError) || error.status !== 429 || error.code !== 'RuntimeQueueFull') return undefined
  return {
    title: '排队已满',
    detail: '当前使用人数较多，排队已满，请稍后重试。',
    code: 'RuntimeQueueFull',
  }
}

/** 排队超时：请求进入了队列，但在限定时间内没有轮到执行。 */
export function describeQueueTimeout(failure: { code?: string }): QueueNotice | undefined {
  if (failure.code !== 'RuntimeQueueTimeout') return undefined
  return {
    title: '排队等待超时',
    detail: '当前排队的请求较多，这次没有轮到执行，请稍后重试。',
    code: 'RuntimeQueueTimeout',
  }
}
