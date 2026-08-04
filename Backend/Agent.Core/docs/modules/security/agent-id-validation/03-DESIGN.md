# AgentIdValidation — 数据模型

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
