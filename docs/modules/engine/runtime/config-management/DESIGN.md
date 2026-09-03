# 配置管理请求链路

## 接口与服务

Agent 和 LLM 配置通过独立的 `ConfigurationController` 暴露，保留 `/api/v1/admin/agents` 和 `/api/v1/admin/llm` 路径。目前宿主只在 Development 环境映射管理端点。

```text
HTTP + authenticated tenant
  -> ConfigurationController
  -> ConfigurationService
  -> IAgentConfigRepository / ILlmConfigRepository
  -> EF Core -> PostgreSQL commit
  -> Redis TTL cache refresh
  -> redacted HTTP response
```

Controller 负责路由、HTTP 验证、Skill 绑定可用性检查、密钥响应脱敏及连接测试。ConfigurationService 合并原 Agent 管理服务、运行时 Provider、数据库缓存包装和 LLM 管理服务，负责租户归属、密钥保留、Repository 调用和缓存。

运行时继续通过 Contracts 中的 IAgentConfigProvider / ILlmConfigProvider 读取配置；DI 把两个接口指向同一个 ConfigurationService，Core 无须引用 Engine 或 EF Core。

## 读取、更新和缓存

- 管理列表和 Agent 配置详情直接读 PostgreSQL；运行时 Agent/LLM 读取和 LLM 详情先读 Redis，未命中或缓存不可用时回源并回填。
- 保存先提交 PostgreSQL，再覆盖对应租户和资源的 Redis 项。Agent 使用版本号检查并发更新；LLM 当前没有乐观并发版本。
- 删除 LLM 先删除数据库记录，再删除缓存。
- TTL 由 ConfigurationStore:RedisCacheTtlSeconds 控制，默认 300 秒；没有周期全量预热、缓存索引、Snapshot、Pub/Sub 或 Mock 配置源。
- Key 使用 `agent:config-cache:v2:{tenant}:{agent}`、`llm:config-cache:v2:{tenant}:{profile}`，租户和资源 ID 分别转义。读取命中后再核对缓存内容中的租户和资源 ID。
- PostgreSQL 与 Redis 不是同一事务。缓存写入/删除失败不会回滚数据库；旧缓存可能保留到 TTL 到期，本范围没有 outbox。

## 执行与功能隔离

```text
ChatRequest(agentId, llmProfileId, fileIds)
  -> AgentExecutor
  -> AgentRuntimeResolver
      -> read Agent and LLM independently
      -> Agent / Model authorization
      -> request-scoped AgentRuntimeProfile
  -> FileAssetRequestResolver
  -> AgentFactory -> model client + history + tools
  -> AIAgent.Run[Streaming]Async
```

Agent 保存指令、轮次、上下文压缩策略及能力绑定；LLM 保存模型连接、ContextTokens、Modality 和加密密钥。执行时才组合两者；LLM 的 ContextTokens 决定有效上下文上限。

图片上传继续使用现有 FileAssetService、PostgreSQL 文件元数据和对象存储。Multimodal 仅控制当前消息和历史消息中图片二进制的受限读取与内联，不新增配置与文件存储的依赖。音频、视频输入暂不开放。

MCP/RAG/Skill 管理接口留在已有能力模块。会话、文件、服务发现和分布式锁使用各自契约、表、缓存键与服务，配置服务不会接管这些功能。

## 密钥

LLM 和 RAG API Key 使用租户绑定的服务端加密值存储在数据库和 Redis 缓存；GET/PUT 响应通过 ConfigurationRedactor 清空 Key。编辑时空 Key 或掩码保留数据库值，连接测试按已认证租户和 Profile ID 补齐并解密保存的 Key。RAG 仍兼容旧的 ApiKeySecretRef。

## 源码

| 层 | 位置 |
|---|---|
| HTTP | Backend/src/OpenAgent.Engine.Host/Controllers/ConfigurationController.cs |
| 配置服务与缓存 | Backend/src/OpenAgent.Engine/Config/ConfigurationService.cs |
| 数据访问 | Backend/src/OpenAgent.Infrastructure/Configuration/ |
| 运行时组合 | Backend/src/OpenAgent.Core/Runtime/Agent/AgentRuntimeResolver.cs |

表结构与迁移见 [配置表](../../../../database/tables/Configurations.md)。
