
## Feature


## 功能定位

权限模块包含两层：`IPermissionEvaluator` 判断用户是否已认证，`IAgentAuthorizationService` 对 Agent、Model、Tool、Function、MCP、Skill 六类运行时资源授权。

## 源码

- `src/Core/Security/IPermissionEvaluator.cs` — 接口定义
- `src/Core/Security/PermissionEvaluator.cs` — 默认实现
- `src/Core/Security/IAgentAuthorizationService.cs` — 细粒度授权扩展接口
- `src/Core/Security/AgentAuthorizationGate.cs` — 统一拒绝语义
- `Agent.Contracts/Security/AgentAuthorization.cs` — 资源维度与授权请求契约

## 核心行为

- `IPermissionEvaluator` 是 public 接口，定义 `IsAuthenticatedAsync` 方法
- 默认实现 `PermissionEvaluator`（internal）仅检查 `IAgentUserContext.IsAuthenticated` 属性
- Auth 中间件依赖此接口进行认证判断
- 未认证时记录 Debug 日志并返回 `false`
- 默认 `AllowAllAgentAuthorizationService` 保持旧配置兼容；生产部署可用 DI 替换
- Agent/Model 在执行初始化阶段校验，Tool/Function/MCP/Skill 在发现和执行阶段校验

## 设计意图

认证与资源授权分离。RBAC/ABAC、租户策略或外部 PDP 应实现 `IAgentAuthorizationService`，无需把策略硬编码进 MAF 或工具适配器。
 — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范

## Specification


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

## Design


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

## Tasks


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

## Tests


## 测试文件

PermissionEvaluator 的测试通过 Auth 中间件测试间接覆盖：

`test/OpenAgent.Core.Tests/Middleware/AuthTests.cs`

## 间接测试覆盖

AuthTests 中的 `FakePermissionEvaluator` 模拟了 `IPermissionEvaluator` 的行为：

| Auth 测试用例 | 覆盖的 PermissionEvaluator 行为 |
|--------------|-------------------------------|
| `InvokeAsync_passes_through_when_authenticated` | `IsAuthenticatedAsync` 返回 `true` 时，Auth 通过 |
| `InvokeAsync_throws_AgentException_when_not_authenticated` | `IsAuthenticatedAsync` 返回 `false` 时，Auth 抛异常 |
| `InvokeStreamAsync_throws_AgentException_when_not_authenticated` | 流式路径下 `IsAuthenticatedAsync` 返回 `false` |

## FakePermissionEvaluator

测试内嵌的 `IPermissionEvaluator` 桩实现：

```csharp
private sealed class FakePermissionEvaluator : IPermissionEvaluator
{
    private readonly bool _isAuthenticated;
    public FakePermissionEvaluator(bool isAuthenticated) => _isAuthenticated = isAuthenticated;
    public Task<bool> IsAuthenticatedAsync(IAgentUserContext userContext, CancellationToken cancellationToken = default)
        => Task.FromResult(_isAuthenticated);
}
```

## 验证要点

- `IsAuthenticatedAsync` 返回 `bool`，Auth 中间件根据结果决定是否抛出异常
- `PermissionEvaluator` 默认实现检查 `userContext.IsAuthenticated` 属性

## 资源授权测试

| 文件 | 覆盖 |
|---|---|
| `Security/AgentAuthorizationGateTests.cs` | 六种资源维度拒绝时统一返回 `PermissionDenied` |
| `Security/ExecutionAuthorizationTests.cs` | Agent 或 Model 被拒绝时不会调用模型引擎 |

Tool/Function/MCP/Skill 的生产路径还需在第二阶段 MAF function middleware 接管工具循环时增加端到端回归。

## Conventions


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
