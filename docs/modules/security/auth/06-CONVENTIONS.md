# Auth — 约定与规范

## 中间件命名

- `Name` 属性固定返回 `"Auth"`（`nameof(Auth)`），即类名本身

## 执行约定

- 认证检查在调用 `next` 之前执行（前置检查）
- 认证失败时立即抛出异常，不继续管道
- 同步和流式路径使用相同的认证逻辑（`EnsureAuthenticatedAsync`）
- 流式路径使用 `WithCancellation(cancellationToken)` 传播取消

## 异常约定

- 认证失败抛出 `AgentException(AgentErrorCode.PermissionDenied, "User is not authenticated")`
- 不使用 HTTP 401/403 语义，由宿主层负责 HTTP 映射

## 日志约定

- 认证失败记录 Warning 级别日志
- 日志包含 `UserId` 以便追踪
- 认证通过不记录日志（避免噪音）

## 依赖约定

- Auth 依赖 `IPermissionEvaluator` 进行实际认证判断
- 默认实现 `PermissionEvaluator` 仅检查 `IsAuthenticated` 属性
- 可通过替换 `IPermissionEvaluator` 实现自定义认证逻辑

## Pipeline 位置约定

- Auth 应作为 Pipeline 的第一个中间件
- 确保所有后续处理都在已认证的上下文中进行
- 其他安全中间件（TenantValidation、AgentIdValidation）应在 Auth 之后

## 扩展约定

- 如需更复杂的认证逻辑（如 M2M 认证、Audience 检查），应扩展 `IPermissionEvaluator` 而非修改 Auth 中间件
- 认证失败不应返回部分结果，必须完全中断执行
