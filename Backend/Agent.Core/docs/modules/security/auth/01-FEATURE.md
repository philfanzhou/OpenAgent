# Auth — 认证中间件

## 功能定位

Auth 中间件是 Agent Pipeline 中的安全关卡，负责验证请求的用户是否已通过认证。未认证用户的请求将被拒绝并抛出异常。

## 源码

`src/Core/Security/Auth.cs`

## 核心行为

- 实现 `IAgentMiddleware`，Name 固定为 `"Auth"`
- 依赖 `IPermissionEvaluator` 进行认证判断
- 调用 `_permissionEvaluator.IsAuthenticatedAsync(userContext, ct)` 检查认证状态
- 未认证时记录 Warning 日志并抛出 `AgentException(PermissionDenied, "User is not authenticated")`
- 同时实现 `InvokeAsync` 和 `InvokeStreamAsync`，共享同一认证逻辑 `EnsureAuthenticatedAsync`

## 在 Pipeline 中的位置

Auth 通常作为 Pipeline 的第一个中间件，确保所有后续处理都在已认证的上下文中进行。

## 相关文档

- [02-SPEC](./02-SPEC.md) — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范
