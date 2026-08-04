# AgentIdValidation — Agent ID 可观测性中间件

## 功能定位

AgentIdValidation 中间件负责检查请求中是否包含有效的 AgentId。与 Auth 和 TenantValidation 不同，AgentId 缺失时不会抛出异常，而是记录 Debug 日志后继续执行，由下游服务决定如何处理。

## 源码

`src/Core/Security/AgentIdValidation.cs`

## 核心行为

- 实现 `IAgentMiddleware`，Name 固定为 `"AgentIdValidation"`
- 检查 `request.AgentId` 是否为 null 或空白字符串
- AgentId 为空时仅记录 Debug 日志，不中断执行
- 同时实现 `InvokeAsync` 和 `InvokeStreamAsync`，共享同一验证逻辑 `ValidateAgentId`
- 验证逻辑为同步操作（不涉及异步 I/O）

## 设计意图

AgentId 的缺失不是致命错误——下游服务会使用默认值作为兜底。此中间件的职责是提供可观测性，而非强制校验。

## 相关文档

- [02-SPEC](./02-SPEC.md) — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范
