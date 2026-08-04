# PermissionEvaluator — 运行时行为

## 认证检查流程

`PermissionEvaluator.IsAuthenticatedAsync`：

1. 检查 `userContext.IsAuthenticated`
2. 为 `false` → 记录 Debug 日志，返回 `Task.FromResult(false)`
3. 为 `true` → 返回 `Task.FromResult(true)`

## 日志行为

| 场景 | 日志级别 | 消息模板 |
|------|---------|---------|
| 用户未认证 | Debug | `"Permission denied: user {UserId} is not authenticated"` |

## 与 Auth 中间件的协作

1. Auth 中间件调用 `IPermissionEvaluator.IsAuthenticatedAsync`
2. 返回 `false` → Auth 抛出 `AgentException(PermissionDenied, "User is not authenticated")`
3. 返回 `true` → Auth 继续管道

## 细粒度授权挂点

| 维度 | 挂点 | Action |
|---|---|---|
| Agent | `IdentityResolution` | `execute` |
| Model | `IdentityResolution` | `invoke` |
| Tool | `ToolAssembler` / `ToolCallDispatcher` | `discover` / `execute` |
| Function | `ToolAssembler` / `ToolCallDispatcher` | `discover` / `execute` |
| MCP | server/binding 发现及 dispatcher | `discover` / `execute` |
| Skill | descriptor 发现及 dispatcher | `discover` / `execute` |

## 当前实现范围

`PermissionEvaluator` 仍只处理认证。细粒度决策委托给可替换的 `IAgentAuthorizationService`；仓库内不提供具体 RBAC/ABAC 规则库。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
