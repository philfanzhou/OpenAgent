# LlmInteractionLogs

`openagent.llm_interaction_logs` 记录每一次发往大模型的交互日志（含工具循环的多次迭代与上下文压缩摘要调用），主键 `InteractionId`，外键级联删除指向 `openagent.conversations`（`ConversationId` 可空，兼容无会话上下文的调用）。表只追加，不做更新。

## 字段定义（C# 契约）

| 字段名 | C# 类型 | 说明 |
|--------|---------|------|
| InteractionId | string | 交互唯一标识（最大长度 64） |
| TenantId | string | 租户（最大长度 256） |
| UserId | string | 用户（最大长度 256） |
| ConversationId | string? | 会话 ID（最大长度 64，可空） |
| TraceId | string | 轮次追溯键（前端 X-Trace-Id；最大长度 256） |
| AgentId | string? | Agent 标识（最大长度 256） |
| Source | int | 调用来源：0=AgentTurn（对话轮次），1=Compaction（压缩摘要） |
| Provider | string? | 提供商（最大长度 256） |
| ApiFormat | string? | API 协议（最大长度 32） |
| ModelId | string | 模型（最大长度 256） |
| Streamed | bool | 是否流式调用 |
| CallIndex | int | 同一轮内的调用序号，从 0 开始 |
| RequestJson | string? | 脱敏请求载荷（jsonb：messages + options） |
| ResponseJson | string? | 脱敏响应载荷（jsonb：contents + usage），失败可能为空 |
| PromptTokens / CompletionTokens / TotalTokens / CachedInputTokens / ReasoningTokens | int? | Provider usage（要求三项核心计数齐备才视为完整） |
| Status | int | 0=Succeeded，1=Failed，2=Cancelled |
| ErrorMessage | string? | 失败/取消摘要（最大长度 1024） |
| StartedAt | DateTimeOffset | 调用开始时间（timestamptz） |
| DurationMs | int | 调用耗时（毫秒） |

## 索引

| 索引 | 用途 |
|------|------|
| `(TenantId, ConversationId, StartedAt)` | 会话维度分页查询（导出/重放） |
| `(TraceId)` | 按轮次定位（跨端追溯） |

## 安全与保留

- 载荷写入前经过脱敏投影：API Key 永不参与序列化；`DataContent` 二进制仅记录媒体类型与字节数占位符（附件字节永不落日志）；超长单字段按 `LlmInteraction:MaxContentLength`（默认 100_000 字符）截断。
- 记录失败只降级为警告日志，绝不影响对话主流程。
- 保留策略跟随会话软删除（数据保留供审计）；当前不做自动清理。
- 可通过 `LlmInteraction:Enabled=false` 关闭。

## 相关迁移

- `20260917012452_AddLlmInteractionTraceability`（同时为 `conversation_messages` 增加 `TraceId` 列）
