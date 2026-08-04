# PermissionEvaluator — 约定与规范

## 接口约定

- `IPermissionEvaluator` 是 public 接口，可被外部实现替换
- 默认实现 `PermissionEvaluator` 仅检查 `IsAuthenticated` 属性
- 自定义实现应保持方法签名的兼容性

## 认证约定

- `IsAuthenticated` 是权限评估的第一道关卡
- 默认实现中，`IsAuthenticated` 为 `false` 时返回 `false`
- 认证检查结果为 `false` 时由 Auth 中间件决定后续行为（抛出异常）

## 用户上下文约定

- `AgentUserContext` 使用 init 属性（record-like 风格）
- `IsAuthenticated` 默认为 `true`（信任上游认证结果）
- `Groups`、`Roles`、`Audience` 为只读列表，不可为 null
- `Claims` 为只读字典，不可为 null

## 扩展约定

- 如需基于角色的权限检查，应扩展 `IPermissionEvaluator`
- 如需基于声明的权限检查，应扩展 `IPermissionEvaluator`
- 新增权限检查维度时不应修改 Auth 中间件
- 资源授权实现通过 DI 替换 `IAgentAuthorizationService`，不得在 MAF Provider 内复制策略
- 发现阶段过滤与执行阶段复核必须同时保留
- `ResourceId` 只能包含业务标识，不得包含 API key、token、完整参数或文件内容

## 日志约定

- 权限拒绝记录 Debug 级别（非 Warning），因为这是正常的业务逻辑
- 日志包含 `UserId` 以便追踪
- 权限通过不记录日志

## 安全约定

- 权限评估失败应采用安全优先策略（deny by default）
- 不应在日志中暴露敏感的认证信息（如 API Key、Token）
- 自定义授权服务异常会中止执行，不能回退为允许

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
