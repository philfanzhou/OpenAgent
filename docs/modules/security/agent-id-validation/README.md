
## Feature


## 功能定位

AgentIdValidation 中间件负责检查请求中是否包含有效的 AgentId。与 Auth 和 TenantValidation 不同，AgentId 缺失时不会抛出异常，而是记录 Debug 日志后继续执行，由下游服务决定如何处理。

## 源码

`Backend/src/OpenAgent.Engine.Host/Extensions/EndpointExtensions.cs`（AgentIdValidation 中间件已移入 Host 端点扩展）

## 核心行为

- 实现 `IAgentMiddleware`，Name 固定为 `"AgentIdValidation"`
- 检查 `request.AgentId` 是否为 null 或空白字符串
- AgentId 为空时仅记录 Debug 日志，不中断执行
- 同时实现 `InvokeAsync` 和 `InvokeStreamAsync`，共享同一验证逻辑 `ValidateAgentId`
- 验证逻辑为同步操作（不涉及异步 I/O）

## 设计意图

AgentId 的缺失不是致命错误——下游服务会使用默认值作为兜底。此中间件的职责是提供可观测性，而非强制校验。
 — 接口契约
- [03-DESIGN](./03-DESIGN.md) — 数据模型
- [04-TASKS](./04-TASKS.md) — 运行时行为
- [05-TESTS](./05-TESTS.md) — 测试覆盖
- [06-CONVENTIONS](./06-CONVENTIONS.md) — 约定与规范

## Specification


## IAgentMiddleware

AgentIdValidation 实现的中间件接口（`OpenAgent.Core.Abstract`）。

| 成员 | 签名 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"AgentIdValidation"` |
| `InvokeAsync` | `Task<AgentResponse> (AgentRequest, IAgentUserContext, AgentPipelineDelegate, CancellationToken)` | 同步执行路径 |
| `InvokeStreamAsync` | `IAsyncEnumerable<string> (AgentRequest, IAgentUserContext, AgentStreamPipelineDelegate, CancellationToken)` | 流式执行路径 |

## AgentRequest

Pipeline 请求模型，AgentIdValidation 检查的关键属性。

| 属性 | 类型 | 说明 |
|------|------|------|
| `AgentId` | `string?` | Agent 标识（可为空） |
| `Query` | `string` | 用户查询 |

## AgentPipelineDelegate

同步管道委托：`delegate Task<AgentResponse> (AgentRequest, IAgentUserContext, CancellationToken)`

## AgentStreamPipelineDelegate

流式管道委托：`delegate IAsyncEnumerable<string> (AgentRequest, IAgentUserContext, CancellationToken)`

## Design


## AgentIdValidation

Agent ID 可观测性中间件实现类（internal，`OpenAgent.Core.Security`）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string` | 固定返回 `"AgentIdValidation"`（`nameof(AgentIdValidation)`） |

构造函数依赖：
- `ILogger<AgentIdValidation> _logger` — 日志记录器

## AgentRequest

Pipeline 请求模型。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Query` | `string` | 用户查询（required, init） |
| `AgentId` | `string?` | Agent 标识（可为空，init） |
| `ConversationId` | `string?` | 会话 ID（init） |
| `TraceId` | `string?` | 追踪 ID（init） |
| `ClientType` | `ClientType` | 客户端类型（init，默认 Web） |
| `IdempotencyKey` | `string?` | 幂等键（init） |
| `ExternalContext` | `Dictionary<string, string>?` | 外部上下文（init） |
| `EnabledSkills` | `List<string>?` | 启用的技能（init） |

## Tasks


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

## Tests


## 测试文件

`Backend/tests/OpenAgent.Engine.Tests/Hosting/`（AgentIdValidation 已移入 Host 端点测试）

## 测试用例

| 用例 | 方法 | 说明 |
|------|------|------|
| `InvokeAsync_passes_through_when_AgentId_is_present` | `InvokeAsync` | AgentId 存在时，请求正常通过管道，返回 next 委托的响应 |
| `InvokeAsync_passes_through_when_AgentId_is_missing` | `InvokeAsync` | AgentId 为 null 时，请求仍正常通过管道（不抛异常） |
| `InvokeStreamAsync_passes_through_when_AgentId_is_missing` | `InvokeStreamAsync` | 流式路径下，AgentId 为 null 时请求正常通过，返回所有 chunk |

## 测试基础设施

### 辅助方法

| 方法 | 说明 |
|------|------|
| `CreateRequest(agentId)` | 创建测试用 `AgentRequest`（Query="test query", AgentId 参数控制） |
| `CreateUserContext()` | 创建测试用 `AgentUserContext`（UserId="user-1", TenantId="tenant-1", IsAuthenticated=true） |
| `NextDelegate` | 返回 `AgentResponse { Content="next-called", Success=true }` |
| `StreamNextDelegate` | 返回两个 chunk `"chunk-1"`, `"chunk-2"` |

## 验证要点

- AgentId 缺失时不抛异常（与 Auth、TenantValidation 的阻断性行为形成对比）
- 流式路径正确传播所有 chunk

## Conventions


## 中间件命名

- `Name` 属性固定返回 `"AgentIdValidation"`（`nameof(AgentIdValidation)`），即类名本身

## 执行约定

- 验证在调用 `next` 之前执行（前置检查）
- 验证结果不影响管道执行（非阻断性检查）
- 同步和流式路径使用相同的验证逻辑（`ValidateAgentId`）
- 验证逻辑为同步操作

## 非阻断性约定

- AgentId 缺失不是致命错误
- 中间件仅提供可观测性（Debug 日志），不抛出异常
- 下游服务负责兜底处理（使用默认 AgentId）
- 这与 Auth 和 TenantValidation 的阻断性检查形成对比

## 日志约定

- AgentId 缺失记录 Debug 级别日志（非 Warning，因为这不是异常情况）
- 日志消息明确说明下游服务会处理
- AgentId 存在时不记录日志

## Pipeline 位置约定

- AgentIdValidation 应在 Auth 和 TenantValidation 之后
- 位置相对灵活，因为它是非阻断性的
- 建议在其他安全检查完成后执行

## 扩展约定

- 如需强制要求 AgentId（阻断性检查），应创建新的中间件而非修改 AgentIdValidation
- 如需 AgentId 格式验证，应扩展 `ValidateAgentId` 方法
- AgentId 的存在性检查和有效性检查应分离
