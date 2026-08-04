# Auth — 运行时行为

## 执行流程

### 同步路径（InvokeAsync）

1. 调用 `EnsureAuthenticatedAsync(userContext, cancellationToken)`
2. 认证通过 → 调用 `next(request, userContext, cancellationToken)` 继续管道
3. 认证失败 → 抛出 AgentException，管道中断

### 流式路径（InvokeStreamAsync）

1. 调用 `EnsureAuthenticatedAsync(userContext, cancellationToken)`
2. 认证通过 → 逐个 `yield return` `next(...)` 的 chunk（带 `WithCancellation`）
3. 认证失败 → 抛出 AgentException，管道中断

## 认证检查逻辑

`EnsureAuthenticatedAsync`（private）：

1. 调用 `_permissionEvaluator.IsAuthenticatedAsync(userContext, cancellationToken)`
2. 返回 `true` → 通过，继续管道
3. 返回 `false` → 记录 Warning 日志，抛出 `AgentException(AgentErrorCode.PermissionDenied, "User is not authenticated")`

## 日志行为

| 场景 | 日志级别 | 消息模板 |
|------|---------|---------|
| 认证失败 | Warning | `"Unauthenticated user {UserId} attempted to access agent"` |

## Pipeline 集成

Auth 作为 `IAgentMiddleware` 注册到 Pipeline，由 Pipeline 按注册顺序逆序构建委托链。Pipeline 捕获 `AgentException` 并转为 `AgentResponse`（`Success=false, ErrorCode=PermissionDenied`）。
