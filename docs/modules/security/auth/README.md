
## Feature


## 功能定位

Auth 中间件是 Agent Pipeline 中的安全关卡，负责验证请求的用户是否已通过认证。未认证用户的请求将被拒绝并抛出异常。

## 源码

`src/Core/Security/Auth.cs`

## 核心行为

- 实现 `IAgentMiddleware`，Name 固定为 `"Auth"`
- 依赖 `IPermissionEvaluator` 进行认证判断
- 调用 `_permissionEvaluator.IsAuthenticatedAsync(userContext, ct)` 检查认证状态
- 未认证时记录 Warning 日志并抛出 `AgentException(PermissionDenied, "User is not authenticated")`
- 同时实现 `InvokeAsync` 和 `InvokeStreamAsync`，共享同一认证逻辑 `EnsureAuthenticatedAsync`

## 在 Pipeline 中的位置

Auth 通常作为 Pipeline 的第一个中间件，确保所有后续处理都在已认证的上下文中进行。
 — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范

## Specification


## IAgentMiddleware

Auth 实现的中间件接口（`OpenAgent.Core.Abstract`）。

| 成员 | 签名 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"Auth"` |
| `InvokeAsync` | `Task<AgentResponse> (AgentRequest, IAgentUserContext, AgentPipelineDelegate, CancellationToken)` | 同步执行路径 |
| `InvokeStreamAsync` | `IAsyncEnumerable<string> (AgentRequest, IAgentUserContext, AgentStreamPipelineDelegate, CancellationToken)` | 流式执行路径 |

## IPermissionEvaluator

Auth 依赖的权限评估接口（`OpenAgent.Contracts.Security`）。

| 方法 | 签名 | 说明 |
|------|------|------|
| `IsAuthenticatedAsync` | `Task<bool> (IAgentUserContext, CancellationToken)` | 判断用户是否已认证 |

实现类：`PermissionEvaluator`（internal）

## IAgentUserContext

用户上下文接口，Auth 检查的关键属性。

| 属性 | 类型 | 说明 |
|------|------|------|
| `IsAuthenticated` | `bool` | 是否已认证 |
| `UserId` | `string` | 用户 ID（用于日志） |

## AgentPipelineDelegate

同步管道委托：`delegate Task<AgentResponse> (AgentRequest, IAgentUserContext, CancellationToken)`

## AgentStreamPipelineDelegate

流式管道委托：`delegate IAsyncEnumerable<string> (AgentRequest, IAgentUserContext, CancellationToken)`

## Design


## Auth

认证中间件实现类（internal，`OpenAgent.Core.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"Auth"`（`nameof(Auth)`） |

构造函数依赖：
- `IPermissionEvaluator _permissionEvaluator` — 权限评估器
- `ILogger<Auth> _logger` — 日志记录器

## AgentException

认证失败时抛出的异常（`OpenAgent.Contracts.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `ErrorCode` | `AgentErrorCode` | 固定为 `PermissionDenied` (100) |
| `Message` | `string?` | `"User is not authenticated"` |
| `Details` | `string?` | null |

继承关系：`AgentException` → `Exception`

## AgentErrorCode.PermissionDenied

错误码值：`100`

## AgentRequest

Pipeline 请求模型（Auth 不直接使用其字段，仅透传给 next）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Query` | `string` | 用户查询（required, init） |
| `AgentId` | `string?` | Agent ID（init） |
| `ConversationId` | `string?` | 会话 ID（init） |
| `TraceId` | `string?` | 追踪 ID（init） |
| `ClientType` | `ClientType` | 客户端类型（init，默认 Web） |
| `IdempotencyKey` | `string?` | 幂等键（init） |
| `ExternalContext` | `Dictionary<string, string>?` | 外部上下文（init） |
| `EnabledSkills` | `List<string>?` | 启用的技能（init） |

## AgentResponse

Pipeline 响应模型。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Content` | `string` | 响应内容（required, init） |
| `Success` | `bool` | 是否成功（init，默认 true） |
| `ErrorCode` | `AgentErrorCode?` | 错误码（init） |
| `ErrorMessage` | `string?` | 错误消息（init） |
| `TraceId` | `string?` | 追踪 ID（init） |
| `Citations` | `List<Citation>?` | 引用列表（init） |
| `ToolCalls` | `List<ToolCallLog>?` | 工具调用日志（init） |
| `TokenUsage` | `TokenUsage?` | Token 用量（init） |

## Tasks


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

## Tests


## 测试文件

`test/OpenAgent.Core.Tests/Middleware/AuthTests.cs`

## 测试用例

| 用例 | 方法 | 说明 |
|------|------|------|
| `InvokeAsync_passes_through_when_authenticated` | `InvokeAsync` | 用户已认证时，请求正常通过管道，返回 next 委托的响应 |
| `InvokeAsync_throws_AgentException_when_not_authenticated` | `InvokeAsync` | 用户未认证时，抛出 `AgentException`，ErrorCode 为 `PermissionDenied` |
| `InvokeStreamAsync_throws_AgentException_when_not_authenticated` | `InvokeStreamAsync` | 流式路径下，用户未认证时抛出 `AgentException` |

## 测试基础设施

### FakePermissionEvaluator

测试内嵌的 `IPermissionEvaluator` 桩实现，通过构造函数参数 `isAuthenticated` 控制返回值。

### 辅助方法

| 方法 | 说明 |
|------|------|
| `CreateRequest()` | 创建测试用 `AgentRequest`（Query="test query", AgentId="agent-1"） |
| `CreateUserContext(isAuthenticated)` | 创建测试用 `AgentUserContext`（UserId="user-1", TenantId="tenant-1"） |
| `NextDelegate` | 返回 `AgentResponse { Content="next-called", Success=true }` |
| `StreamNextDelegate` | 返回单个 chunk `"chunk-1"` |

## 错误码验证

| 错误码 | 值 | 测试覆盖 |
|--------|-----|---------|
| `PermissionDenied` | 100 | ✅ `InvokeAsync_throws_AgentException_when_not_authenticated` |

## Conventions


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
