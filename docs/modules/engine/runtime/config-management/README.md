# Config Management

Agent 与 LLM 配置均以 PostgreSQL 为唯一事实源，Redis 只保存可删除、带 TTL、按租户隔离的派生缓存。

## 读取与更新

```text
执行请求(agentId, llmProfileId, tenantId)
  ├─ Agent: Redis TTL cache → PostgreSQL → 回填 Redis
  └─ LLM:   Redis TTL cache → PostgreSQL → 回填 Redis

管理端更新
  └─ PostgreSQL commit → 立即刷新当前 Redis cache
```

- Agent 表：`openagent.agent_configurations`，主键 `(TenantId, AgentId)`。
- LLM 表：`openagent.llm_configurations`，主键 `(TenantId, ProfileId)`。
- Agent 缓存：`agent:config-cache:{tenantId}:{agentId}`。
- LLM 缓存：`llm:config-cache:{tenantId}:{profileId}`。
- TTL 默认 300 秒；Agent 缓存另有默认 60 秒的周期回填。
- Redis 不可用时直接读写 PostgreSQL，不启用内存 Snapshot 或 Mock 配置源。

## 配置边界

- Agent 只保存指令、上下文策略、轮次限制和 MCP/Skill/RAG 绑定，不保存 LLM 绑定。
- 执行请求必须显式提供 `llmProfileId`。
- LLM Profile 保存 Model ID、上下文窗口、协议、Endpoint、Temperature 和明文 API Key。
- API Key 可在 PostgreSQL 与 Redis 服务端缓存中出现，但管理 API 永不返回，日志也不得输出。
- 编辑已有 LLM 时提交空 Key 表示保留数据库中的原 Key；连接测试会按租户加载真实 Key。
- RAG 仍使用 `ApiKeySecretRef`，不接受内联明文 Key。

## 一致性

PostgreSQL 提交与 Redis 刷新不是跨存储事务。数据库提交成功、Redis 刷新失败时，事实数据仍有效；后续缓存未命中或 TTL 到期会从 PostgreSQL 恢复。当前范围不包含 Pub/Sub、Snapshot 或 outbox。

## Source

- Contracts: `IAgentConfigRepository`, `ILlmConfigRepository`, `ILlmConfigProvider`
- Engine: `AgentConfigDatabaseStore`, `LlmProfileManagementService`, `ConfigProvider`
- Infrastructure: `EfCoreAgentConfigRepository`, `EfCoreLlmConfigRepository`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Config/`, `Backend/tests/OpenAgent.Infrastructure.Tests/`
