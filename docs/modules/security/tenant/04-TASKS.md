# TenantValidation — 运行时行为

## 执行流程

### 同步路径（InvokeAsync）

1. 调用 `EnsureTenant(userContext)`
2. 验证通过 → 调用 `next(request, userContext, cancellationToken)` 继续管道
3. 验证失败 → 抛出 TenantDataIsolationException，管道中断

### 流式路径（InvokeStreamAsync）

1. 调用 `EnsureTenant(userContext)`
2. 验证通过 → 逐个 `yield return` `next(...)` 的 chunk（带 `WithCancellation`）
3. 验证失败 → 抛出 TenantDataIsolationException，管道中断

## 验证逻辑

`EnsureTenant`（private，同步方法）：

1. 检查 `string.IsNullOrEmpty(userContext.TenantId)`
2. 为空 → 记录 Warning 日志，抛出 `TenantDataIsolationException(null, null, "TenantId is required but not provided")`
3. 非空 → 通过

## 日志行为

| 场景 | 日志级别 | 消息模板 |
|------|---------|---------|
| TenantId 缺失 | Warning | `"TenantId is missing from user context for user {UserId}"` |

## Pipeline 集成

TenantValidation 作为 `IAgentMiddleware` 注册到 Pipeline。Pipeline 捕获 `AgentException`（`TenantDataIsolationException` 的基类）并转为 `AgentResponse`（`Success=false, ErrorCode=TenantDataIsolationViolation`）。
