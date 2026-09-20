# 会话持久化

`IConversationStore` 是数据库无关契约。当前 `OpenAgent.Infrastructure` 使用 EF Core + PostgreSQL
作为持久化实现，不支持其他数据库 Provider。

生产链路使用写穿透组合：PostgreSQL 先完成会话、消息与文件引用的事务性写入，成功后把完整会话
写入 Redis 热副本。读取优先命中 Redis，未命中时从 PostgreSQL 回填。Redis 从不作为事实源，
缓存失败不会回滚已提交的数据库写入。

每次成功 assistant 响应把 Provider 终态 `TokenUsage` 与 `ModelId` 保存到对应消息；取消、失败或 Provider 未返回完整 usage 时保持 `TokenUsage=null`。会话累计值由持久化消息按 `MessageId` 派生，不保存可漂移的独立计数器，因此重载历史、切换会话和缓存回填不会重复累计。只要任一响应缺 usage，完整会话累计即不可精确计算。

`IConversationLock` 是独立于存储的协调契约：Redis 已配置时使用带租约心跳的分布式锁，保证同一
`{tenantId}:{conversationId}` 在多个 Engine 实例间串行执行；未配置 Redis 的单实例开发环境使用
进程内锁。两种锁都不承担数据存储职责。

## 交互日志与轮次追溯

所有发往大模型的交互（Agent 轮次内的每次调用，含工具循环迭代；自动/手动压缩摘要调用）由
`LlmInteractionRecorder`（挂在 `AgentChatClientFactory` 产出的 provider client 最外层）捕获，
写入 `openagent.llm_interaction_logs`（见 [LlmInteractionLogs](../../../database/tables/LlmInteractionLogs.md)），
同时输出一条摘要结构化日志（EventId 1460/1461）。载荷经过脱敏投影：API Key 永不落日志，
二进制内容仅记录占位符，超长字段按 `LlmInteraction:MaxContentLength` 截断。记录失败只降级为警告，
绝不影响对话主流程；`LlmInteraction:Enabled=false` 可整体关闭。

轮次关联键是每次请求携带的 `X-Trace-Id`：同一轮内所有 LLM 调用与该轮全部落库消息
（`conversation_messages.TraceId`，由 `ConversationSessionStore.SaveAsync` 统一盖章）共享同一个值，
据此可把消息时间线与交互日志按轮次对齐做原始会话状态分析。

查询 API：`GET /api/v1/agent/conversations/{id}/llm-interactions`（租户 + 属主校验，按
`(StartedAt, CallIndex)` 升序分页）。Router 已注册同路径转发。
