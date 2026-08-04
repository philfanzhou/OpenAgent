# TenantValidation — 约定与规范

## 中间件命名

- `Name` 属性固定返回 `"TenantValidation"`（`nameof(TenantValidation)`），即类名本身

## 执行约定

- 租户验证在调用 `next` 之前执行（前置检查）
- 验证失败时立即抛出异常，不继续管道
- 同步和流式路径使用相同的验证逻辑（`EnsureTenant`）
- 验证逻辑为同步操作（不涉及异步 I/O）

## 异常约定

- 验证失败抛出 `TenantDataIsolationException(null, null, "TenantId is required but not provided")`
- 使用专用的 `TenantDataIsolationException` 而非通用 `AgentException`
- 异常的 `TenantId` 和 `RequestedTenantId` 均为 null（因为原始值缺失）

## 日志约定

- 验证失败记录 Warning 级别日志
- 日志包含 `UserId` 以便追踪
- 验证通过不记录日志

## Pipeline 位置约定

- TenantValidation 应在 Auth 中间件之后
- 确保在已认证的上下文中检查租户信息
- 其他依赖 TenantId 的中间件应在 TenantValidation 之后

## 数据隔离约定

- TenantId 是多租户数据隔离的基础字段
- 所有涉及数据存储的操作都应依赖 TenantId
- TenantId 缺失应被视为严重错误，不可静默忽略
- TenantId 验证是数据隔离的第一道防线

## 扩展约定

- 如需更复杂的租户验证（如租户存在性检查、租户状态检查），应扩展此中间件或添加新中间件
- 不应在 TenantValidation 中混入业务逻辑
- 租户间的数据隔离由存储层保证，中间件只确保 TenantId 存在
