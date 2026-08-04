# PermissionEvaluator — 数据模型

## PermissionEvaluator

权限评估默认实现（internal，`OpenAgent.Core.Security`）。

构造函数依赖：
- `ILogger<PermissionEvaluator> _logger` — 日志记录器

无公开属性，仅实现 `IPermissionEvaluator.IsAuthenticatedAsync`。

## AgentUserContext

用户上下文默认实现（`OpenAgent.Contracts.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `UserId` | `string` | 用户 ID（required, init） |
| `TenantId` | `string?` | 租户 ID（init，默认 null） |
| `Groups` | `IReadOnlyList<string>` | 用户组（init，默认 `[]`） |
| `Roles` | `IReadOnlyList<string>` | 用户角色（init，默认 `[]`） |
| `Claims` | `IReadOnlyDictionary<string, string>` | 用户声明（init，默认空字典） |
| `Audience` | `IReadOnlyList<string>` | 受众（init，默认 `[]`） |
| `IsAuthenticated` | `bool` | 是否已认证（init，默认 true） |

## AgentErrorCode 权限相关值

| 错误码 | 值 | 说明 |
|--------|-----|------|
| `PermissionDenied` | 100 | 权限拒绝 |
| `AudiencePermissionDenied` | 6001 | 受众权限拒绝 |
| `AudienceMismatch` | 6002 | 受众不匹配 |

## 资源授权数据流

```text
IAgentUserContext + AgentAuthorizationRequest
    -> IAgentAuthorizationService
    -> AgentAuthorizationGate
    -> allow 或 AgentException(PermissionDenied)
```

发现阶段先过滤模型可见的 Skill、MCP binding 和 Tool；执行阶段再次校验，避免缓存或模型生成未公开函数名时绕过。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
