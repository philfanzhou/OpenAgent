# security — 安全与租户

security 域负责 Agent Pipeline 的安全关卡，包括认证、租户隔离、AgentId 可观测性及权限评估。

## 功能点

| 功能点 | 类 | 说明 | 文档 |
|--------|-----|------|------|
| auth | `Auth` | 认证中间件，未认证请求抛 AgentException(PermissionDenied) | [auth/](./auth/) |
| tenant | `TenantValidation` | 租户校验中间件，TenantId 缺失抛 TenantDataIsolationException | [tenant/](./tenant/) |
| agent-id-validation | `AgentIdValidation` | AgentId 可观测性中间件，缺失时仅记录 Debug 日志 | [agent-id-validation/](./agent-id-validation/) |
| permission | `PermissionEvaluator` | 权限评估器，检查 IAgentUserContext.IsAuthenticated | [permission/](./permission/) |

## 源码位置

- `src/Core/Security/Auth.cs`
- `src/Core/Security/TenantValidation.cs`
- `src/Core/Security/AgentIdValidation.cs`
- `src/Core/Security/PermissionEvaluator.cs`
- `src/Core/Security/IPermissionEvaluator.cs`

## 依赖关系

```
Auth ──依赖──▶ IPermissionEvaluator ──实现──▶ PermissionEvaluator
TenantValidation（无外部依赖，仅依赖 ILogger）
AgentIdValidation（无外部依赖，仅依赖 ILogger）
```

## Pipeline 执行顺序

推荐顺序：Auth → TenantValidation → AgentIdValidation → ...

- Auth 确保已认证
- TenantValidation 确保租户隔离
- AgentIdValidation 提供可观测性（非阻断）
