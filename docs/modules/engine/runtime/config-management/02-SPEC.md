# ConfigManagement - 功能规格说明

## 功能需求 (FR)

### FR-CM-001: 配置快照读取

- **方法签名**: `T? GetConfig<T>(string key)` / `T? GetConfig<T>(string agentId, string configType)`
- **行为**: 从 IMemoryCache 读取缓存配置
- **TryGetConfig**: `bool TryGetConfig<T>(string key, out T? config)` / `bool TryGetConfig<T>(string agentId, string configType, out T? config)`

### FR-CM-002: 配置快照写入

- **方法签名**: `void SetConfig<T>(string key, T value)` / `void SetConfig<T>(string agentId, string configType, T value)`
- **行为**: 使用 `lock` 保护写入 IMemoryCache
- **缓存 key 格式**: `agent:{agentId}:config:{configType}`
- **版本 key 格式**: `agent:{agentId}:config:{configType}:version`

### FR-CM-003: 全量配置写入

- **方法签名**: `void SetFullConfig(string agentId, AgentConfig config, long? version = null)`
- **行为**: 一次性写入 5 个子配置：FullAgentConfig、LLMSettings、RAGSettings、MCPSettings、SkillsSettings
- **版本同步**: 若 version 有值，同步设置所有子配置的版本号

### FR-CM-004: 版本管理

- **方法签名**: `long? GetVersion(string key)` / `long? GetVersion(string agentId, string configType)`
- **写入**: `void SetVersion(string key, long value)` / `void SetVersion(string agentId, string configType, long value)`

### FR-CM-005: 配置提供 - 无 agentId 调用

- **方法签名**: `Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default)`
- **行为**: 始终抛出 `InvalidOperationException`，消息为 "GetConfigAsync() without agentId is not supported. Use GetConfigAsync(string agentId) instead."

### FR-CM-006: 配置提供 - 三级读取链

- **方法签名**: `Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default)`
- **读取链**:
  1. **Snapshot**: 尝试从 ConfigSnapshot 读取 FullAgentConfig，或组装各子配置片段
  2. **Redis**: 读取 `agent:config:{agentId}`，反序列化为 `AgentConfigEntity`，写入 Snapshot
  3. **Mock 降级**: 若 AllowMockAgent=true，返回 Mock 配置
  4. **返回 null**: 以上均无结果

### FR-CM-007: 敏感信息注入

- **方法**: `EnrichWithSecureSecrets(AgentConfig config)`
- **行为**: 若 `config.Llm.ApiKey` 为空，从环境变量 `LLM__APIKEY` 或 `LLM_API_KEY` 读取
- **优先级**: `LLM__APIKEY` > `LLM_API_KEY` > 空字符串

### FR-CM-008: AllowMockAgent 解析

- **优先级**: 配置值 `Engine:AllowMockAgent` > 环境变量 `ALLOW_MOCK_AGENT` > `IsDevelopment() || IsEnvironment("Testing")`

### FR-CM-009: Agent 列表查询

- **方法签名**: `Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default)`
- **行为**: 读取 `agent:published:index` Redis Set，遍历每个 agentId 读取 `agent:config:{agentId}`，反序列化为 AgentSummary

### FR-CM-010: Mock 配置生成

- **方法**: `CreateMockFallbackConfig()`
- **返回**: `AgentConfig { FrameworkType=Mock, Llm=new LlmConfig(), Rag=new RagConfig{Enabled=false}, Mcp=new McpConfig() }`

## 验收标准 (AC)

### AC-CM-001: 无 agentId 调用抛出异常

- **Given** 任意 ConfigProvider 实例
- **When** 调用 `GetConfigAsync()` 无 agentId 重载
- **Then** 抛出 `InvalidOperationException`

### AC-CM-002: Snapshot 命中直接返回

- **Given** Snapshot 中已有 agent 的 FullAgentConfig
- **When** 调用 `GetConfigAsync(agentId)`
- **Then** 直接返回 Snapshot 中的配置，不访问 Redis

### AC-CM-003: Redis 读取并缓存

- **Given** Snapshot 中无配置，Redis 中有 `agent:config:{agentId}`
- **When** 调用 `GetConfigAsync(agentId)`
- **Then** 从 Redis 读取，写入 Snapshot，返回配置

### AC-CM-004: Mock 降级

- **Given** Snapshot 和 Redis 均无配置，AllowMockAgent=true
- **When** 调用 `GetConfigAsync(agentId)`
- **Then** 返回 Mock 配置（FrameworkType=Mock）

### AC-CM-005: 无配置返回 null

- **Given** Snapshot 和 Redis 均无配置，AllowMockAgent=false
- **When** 调用 `GetConfigAsync(agentId)`
- **Then** 返回 null

### AC-CM-006: API Key 从环境变量注入

- **Given** Agent 配置中 Llm.ApiKey 为空，环境变量 `LLM__APIKEY=sk-xxx`
- **When** 从 Snapshot 读取配置
- **Then** `config.Llm.ApiKey == "sk-xxx"`

### AC-CM-007: SetFullConfig 写入所有子配置

- **Given** 一个包含 Llm、Rag、Mcp、Skills 的 AgentConfig
- **When** 调用 `SetFullConfig(agentId, config, version=5)`
- **Then** FullAgentConfig、LLMSettings、RAGSettings、MCPSettings、SkillsSettings 均可独立读取，版本均为 5

### AC-CM-008: Redis 不可用时跳过 Redis 读取

- **Given** Redis `IsAvailable == false`
- **When** 调用 `GetConfigAsync(agentId)`
- **Then** 跳过 Redis 读取，日志记录 "Entering island mode"

### AC-CM-009: ListAgentsAsync 返回已发布 Agent 列表

- **Given** Redis 中 `agent:published:index` 包含 agent-a、agent-b
- **When** 调用 `ListAgentsAsync()`
- **Then** 返回包含两个 AgentSummary 的列表

## 配置项

| 配置路径 | 默认值 | 说明 |
|---------|--------|------|
| Engine:AllowMockAgent | null | 是否允许 Mock Agent 降级 |
| 环境变量 ALLOW_MOCK_AGENT | null | Mock Agent 环境变量开关 |
| 环境变量 LLM__APIKEY | null | LLM API Key |
| 环境变量 LLM_API_KEY | null | LLM API Key（备选） |
