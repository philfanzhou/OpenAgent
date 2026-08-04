# TenantValidation — 租户校验中间件

## 功能定位

TenantValidation 中间件确保请求的用户上下文中包含有效的 TenantId，实现多租户数据隔离的基础保障。缺少 TenantId 的请求将被拒绝并抛出 `TenantDataIsolationException`。

## 源码

`src/Core/Security/TenantValidation.cs`

## 核心行为

- 实现 `IAgentMiddleware`，Name 固定为 `"TenantValidation"`
- 检查 `userContext.TenantId` 是否为 null 或空字符串
- TenantId 为空时记录 Warning 日志并抛出 `TenantDataIsolationException(null, null, "TenantId is required but not provided")`
- 同时实现 `InvokeAsync` 和 `InvokeStreamAsync`，共享同一验证逻辑 `EnsureTenant`
- 验证逻辑为同步操作（不涉及异步 I/O）

## 在 Pipeline 中的位置

TenantValidation 通常在 Auth 中间件之后执行，确保在已认证的上下文中检查租户信息。

## 相关文档

- [02-SPEC](./02-SPEC.md) — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范
