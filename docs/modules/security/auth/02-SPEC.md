# Auth — 接口契约

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
