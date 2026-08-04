# Errors 统一错误处理与错误码

## 核心用户故事

作为 Agent 系统的开发者和运维者，我希望执行过程中的异常被正确分类、传播和记录，以便快速定位问题、区分可恢复与不可恢复错误，并确保异常不会导致会话数据丢失。

## 功能名称和一句话概括

统一错误处理 — 定义执行层异常分类、传播规则和会话写回保障，确保异常不吞没、不丢失上下文。

## 补充约束

- Core 层定义异常语义，不越界定义宿主层响应协议
- AgentException 携带 ErrorCode，用于结构化错误分类
- 工具执行异常（非 AgentException）返回错误文本，不中断推理循环
- 取消和失败时必须写回已产生的 partial 消息
- 异常日志必须包含 TraceId/AgentId/TenantId 等追踪字段

## 关键验收条件摘要

- [ ] AgentException 携带 ErrorCode 和 Details
- [ ] Pipeline 捕获 AgentException 并转为 AgentResponse（Success=false）
- [ ] 工具执行异常返回错误文本而非抛出
- [ ] 取消时写回 partial 消息并标记 Cancelled
- [ ] 失败时写回 partial 消息并标记 Failed
- [ ] 异常日志包含足够定位问题的上下文

## 明确列出"范围外"

- HTTP 状态码映射（属宿主层）
- SSE 错误事件格式（属宿主层）
- 全局异常中间件行为（属宿主层）

## 文档索引

- [02-SPEC.md](./02-SPEC.md) — 详细需求规格
- [03-DESIGN.md](./03-DESIGN.md) — 设计说明
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试计划
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 约定与规范
