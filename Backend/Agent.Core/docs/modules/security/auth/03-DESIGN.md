# Auth — 数据模型

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
