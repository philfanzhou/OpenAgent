# PermissionEvaluator — 接口契约

## IPermissionEvaluator

权限评估接口（public，`OpenAgent.Contracts.Security`）。

| 方法 | 签名 | 说明 |
|------|------|------|
| `IsAuthenticatedAsync` | `Task<bool> (IAgentUserContext userContext, CancellationToken cancellationToken = default)` | 判断用户是否已认证 |

实现类：`PermissionEvaluator`（internal）

## IAgentAuthorizationService

```csharp
Task<bool> IsAuthorizedAsync(
    AgentAuthorizationRequest request,
    IAgentUserContext userContext,
    CancellationToken cancellationToken = default);
```

`AgentAuthorizationRequest` 包含 `AgentId`、`ResourceType`、`ResourceId` 和 `Action`。`AgentResourceType` 固定为 `Agent`、`Model`、`Tool`、`Function`、`Mcp`、`Skill`。

## IAgentUserContext

用户上下文接口，权限评估的关键数据源。

| 属性 | 类型 | 说明 |
|------|------|------|
| `UserId` | `string` | 用户 ID |
| `TenantId` | `string?` | 租户 ID |
| `Groups` | `IReadOnlyList<string>` | 用户组 |
| `Roles` | `IReadOnlyList<string>` | 用户角色 |
| `Claims` | `IReadOnlyDictionary<string, string>` | 用户声明 |
| `Audience` | `IReadOnlyList<string>` | 受众 |
| `IsAuthenticated` | `bool` | 是否已认证 |

## AgentUserContext

`IAgentUserContext` 的默认实现。

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `UserId` | `string` | 必填（required, init） | 用户 ID |
| `TenantId` | `string?` | null | 租户 ID |
| `Groups` | `IReadOnlyList<string>` | `[]` | 用户组 |
| `Roles` | `IReadOnlyList<string>` | `[]` | 用户角色 |
| `Claims` | `IReadOnlyDictionary<string, string>` | 空 | 用户声明 |
| `Audience` | `IReadOnlyList<string>` | `[]` | 受众 |
| `IsAuthenticated` | `bool` | true | 是否已认证 |

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
