
## Feature


## 功能定位

TenantValidation 中间件确保请求的用户上下文中包含有效的 TenantId，实现多租户数据隔离的基础保障。缺少 TenantId 的请求将被拒绝并抛出 `TenantDataIsolationException`。

## 源码

`src/Core/Security/TenantValidation.cs`

## 核心行为

- 实现 `IAgentMiddleware`，Name 固定为 `"TenantValidation"`
- 检查 `userContext.TenantId` 是否为 null 或空字符串
- TenantId 为空时记录 Warning 日志并抛出 `TenantDataIsolationException(null, null, "TenantId is required but not provided")`
- 同时实现 `InvokeAsync` 和 `InvokeStreamAsync`，共享同一验证逻辑 `EnsureTenant`
- 验证逻辑为同步操作（不涉及异步 I/O）

## 在 Pipeline 中的位置

TenantValidation 通常在 Auth 中间件之后执行，确保在已认证的上下文中检查租户信息。
 — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范

## Specification


## IAgentMiddleware

TenantValidation 实现的中间件接口（`OpenAgent.Core.Abstract`）。

| 成员 | 签名 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"TenantValidation"` |
| `InvokeAsync` | `Task<AgentResponse> (AgentRequest, IAgentUserContext, AgentPipelineDelegate, CancellationToken)` | 同步执行路径 |
| `InvokeStreamAsync` | `IAsyncEnumerable<string> (AgentRequest, IAgentUserContext, AgentStreamPipelineDelegate, CancellationToken)` | 流式执行路径 |

## IAgentUserContext

用户上下文接口，TenantValidation 检查的关键属性。

| 属性 | 类型 | 说明 |
|------|------|------|
| `TenantId` | `string?` | 租户 ID |
| `UserId` | `string` | 用户 ID（用于日志） |

## AgentPipelineDelegate

同步管道委托：`delegate Task<AgentResponse> (AgentRequest, IAgentUserContext, CancellationToken)`

## AgentStreamPipelineDelegate

流式管道委托：`delegate IAsyncEnumerable<string> (AgentRequest, IAgentUserContext, CancellationToken)`

## Design


## TenantValidation

租户校验中间件实现类（internal，`OpenAgent.Core.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"TenantValidation"`（`nameof(TenantValidation)`） |

构造函数依赖：
- `ILogger<TenantValidation> _logger` — 日志记录器

## TenantDataIsolationException

租户数据隔离异常（`OpenAgent.Contracts.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `ErrorCode` | `AgentErrorCode` | 固定为 `TenantDataIsolationViolation` (5003) |
| `TenantId` | `string?` | 用户的租户 ID（此处为 null） |
| `RequestedTenantId` | `string?` | 请求的租户 ID（此处为 null） |
| `Message` | `string?` | `"TenantId is required but not provided"` |
| `Details` | `string?` | `"{tenantId} vs {requestedTenantId}"`（此处为 " vs "） |

继承关系：`TenantDataIsolationException` → `AgentException` → `Exception`

## AgentErrorCode 租户相关值

| 错误码 | 值 | 说明 |
|--------|-----|------|
| `TenantMismatch` | 5001 | 租户不匹配 |
| `TenantNotFound` | 5002 | 租户未找到 |
| `TenantDataIsolationViolation` | 5003 | 租户数据隔离违规 |

## Tasks


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

## Tests


## 测试文件

`test/OpenAgent.Core.Tests/Middleware/TenantValidationTests.cs`

## 测试用例

| 用例 | 方法 | 说明 |
|------|------|------|
| `InvokeAsync_passes_through_when_TenantId_is_present` | `InvokeAsync` | TenantId 存在时，请求正常通过管道 |
| `InvokeAsync_throws_TenantDataIsolationException_when_TenantId_is_missing` | `InvokeAsync` | TenantId 为 null 时，抛出 `TenantDataIsolationException`，ErrorCode 为 `TenantDataIsolationViolation` |
| `InvokeStreamAsync_throws_TenantDataIsolationException_when_TenantId_is_missing` | `InvokeStreamAsync` | 流式路径下，TenantId 为 null 时抛出 `TenantDataIsolationException` |

## 测试基础设施

### 辅助方法

| 方法 | 说明 |
|------|------|
| `CreateRequest()` | 创建测试用 `AgentRequest`（Query="test query", AgentId="agent-1"） |
| `CreateUserContext(tenantId)` | 创建测试用 `AgentUserContext`（UserId="user-1", TenantId 参数控制, IsAuthenticated=true） |
| `NextDelegate` | 返回 `AgentResponse { Content="next-called", Success=true }` |
| `StreamNextDelegate` | 返回单个 chunk `"chunk-1"` |

## 错误码验证

| 错误码 | 值 | 测试覆盖 |
|--------|-----|---------|
| `TenantDataIsolationViolation` | 5003 | ✅ `InvokeAsync_throws_TenantDataIsolationException_when_TenantId_is_missing` |

## Conventions


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
