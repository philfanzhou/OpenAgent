# ErrorHandling - 功能概述

## 核心用户故事

作为客户端应用，我希望在请求失败时收到有意义的错误响应，以便能正确处理异常情况。

## 功能简介

ErrorHandling 模块提供了 Agent.Engine 的全局异常处理机制，确保所有未处理异常都能被转换为结构化的错误响应。模块包含两个中间件：`GlobalExceptionHandlerMiddleware` 处理常规 HTTP 请求的异常（返回 ProblemDetails），`SseErrorHandlerMiddleware` 专门处理 SSE 端点的异常（返回 SSE 格式错误事件）。同时，`StreamingPayloadFactory` 为流式端点提供统一的错误载荷构造。

## 组件一览

| 组件 | 职责 |
|------|------|
| GlobalExceptionHandlerMiddleware | 捕获所有未处理异常，映射为 ProblemDetails 响应 |
| SseErrorHandlerMiddleware | SSE 端点异常处理，以 SSE 事件格式输出错误 |
| StreamingPayloadFactory | 构造流式错误载荷（StreamingErrorPayload） |

## 异常映射概览

| 异常类型 | HTTP 状态码 | 错误类型 URI |
|----------|-------------|-------------|
| UnauthorizedAccessException | 403 | https://error.agent.com/unauthorized |
| HumanApprovalRequiredException | 202 | https://error.agent.com/approval-required |
| AgentException | 按 ErrorCode 映射 | https://error.agent.com/{errorcode} |
| TimeoutException | 504 | https://error.agent.com/timeout |
| 其他 | 500 | https://error.agent.com/internal-error |

## 相关文档

- [02-SPEC - 详细规格](./02-SPEC.md)
- [03-DESIGN - 设计文档](./03-DESIGN.md)
- [04-TASKS - 任务清单](./04-TASKS.md)
- [05-TESTS - 测试文档](./05-TESTS.md)
- [06-CONVENTIONS - 约定规范](./06-CONVENTIONS.md)
