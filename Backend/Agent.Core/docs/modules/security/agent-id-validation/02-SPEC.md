# AgentIdValidation — 接口契约

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
