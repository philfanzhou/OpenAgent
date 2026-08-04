# AgentIdValidation — 运行时行为

## 执行流程

### 同步路径（InvokeAsync）

1. 调用 `ValidateAgentId(request)`
2. 无论验证结果如何，都调用 `next(request, userContext, cancellationToken)` 继续管道

### 流式路径（InvokeStreamAsync）

1. 调用 `ValidateAgentId(request)`
2. 无论验证结果如何，都逐个 `yield return` `next(...)` 的 chunk（带 `WithCancellation`）

## 验证逻辑

`ValidateAgentId`（private，同步方法）：

1. 检查 `string.IsNullOrWhiteSpace(request.AgentId)`
2. 为空 → 记录 Debug 日志，继续执行
3. 非空 → 直接通过（无日志）

## 日志行为

| 场景 | 日志级别 | 消息模板 |
|------|---------|---------|
| AgentId 缺失 | Debug | `"Agent invocation received without explicit AgentId. Downstream service will resolve the effective agent id."` |

## Pipeline 集成

AgentIdValidation 作为 `IAgentMiddleware` 注册到 Pipeline，不抛出异常，不影响管道执行。
