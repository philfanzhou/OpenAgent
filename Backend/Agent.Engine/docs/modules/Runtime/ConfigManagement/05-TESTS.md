# ConfigManagement - 测试文档

## 现有测试

### ConfigProviderTests（`test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs`）

| 测试方法 | 场景 |
|---------|------|
| `GetConfigAsync_without_agentId_throws` | 无 agentId 调用抛出 InvalidOperationException |
| `GetConfigAsync_with_empty_agentId_returns_mock_when_allowed` | 空 agentId + AllowMock=true → 返回 Mock 配置 |
| `GetConfigAsync_with_empty_agentId_returns_null_when_not_allowed` | 空 agentId + AllowMock=false → 返回 null |
| `GetConfigAsync_loads_from_snapshot` | Snapshot 中有 FullAgentConfig → 直接返回 |
| `GetConfigAsync_loads_from_redis_when_snapshot_miss` | Snapshot 未命中 → 从 Redis 读取并缓存 |
| `GetConfigAsync_returns_mock_fallback_when_nothing_found_and_allowed` | 均无配置 + AllowMock=true → Mock 降级 |
| `GetConfigAsync_returns_null_when_nothing_found_and_not_allowed` | 均无配置 + AllowMock=false → null |

### ConfigSnapshotTests（`test/OpenAgent.Engine.Tests/Config/ConfigSnapshotTests.cs`）

| 测试方法 | 场景 |
|---------|------|
| `SetConfig_and_GetConfig_roundtrip` | 按 key 写入/读取 LlmConfig |
| `TryGetConfig_returns_false_for_missing_key` | 缺失 key 返回 false |
| `SetVersion_and_GetVersion_roundtrip` | 版本号写入/读取 |
| `SetFullConfig_stores_all_sub_configs` | 全量写入后各子配置可独立读取 |
| `GetVersion_returns_null_for_missing_key` | 缺失 key 版本返回 0（方法名有误，实际返回 `(long?)0` 而非 null） |

## 缺失测试场景

### TC-CM-001: Redis 不可用时进入孤岛模式

- **Given** Redis `IsAvailable == false`
- **When** 调用 `GetConfigAsync(agentId)` 且 Snapshot 无缓存
- **Then** 日志记录 "Entering island mode"，跳过 Redis 读取

### TC-CM-002: 敏感信息注入 - LLM__APIKEY 优先

- **Given** 环境变量 `LLM__APIKEY=sk-primary`，`LLM_API_KEY=sk-secondary`
- **When** 从 Snapshot 读取配置（Llm.ApiKey 为空）
- **Then** `config.Llm.ApiKey == "sk-primary"`

### TC-CM-003: 敏感信息注入 - LLM_API_KEY 回退

- **Given** 环境变量 `LLM__APIKEY` 未设置，`LLM_API_KEY=sk-secondary`
- **When** 从 Snapshot 读取配置（Llm.ApiKey 为空）
- **Then** `config.Llm.ApiKey == "sk-secondary"`

### TC-CM-004: 敏感信息注入 - 已有 Key 不覆盖

- **Given** 配置中 `Llm.ApiKey = "existing-key"`
- **When** 从 Snapshot 读取配置
- **Then** `config.Llm.ApiKey == "existing-key"`（环境变量不覆盖）

### TC-CM-005: AllowMockAgent - 配置值优先

- **Given** 配置 `Engine:AllowMockAgent=true`，环境为 Production
- **When** 解析 AllowMockAgent
- **Then** 返回 true

### TC-CM-006: AllowMockAgent - 环境变量次之

- **Given** 配置未设置，环境变量 `ALLOW_MOCK_AGENT=true`，环境为 Production
- **When** 解析 AllowMockAgent
- **Then** 返回 true

### TC-CM-007: AllowMockAgent - Development 环境默认

- **Given** 配置和环境变量均未设置，环境为 Development
- **When** 解析 AllowMockAgent
- **Then** 返回 true

### TC-CM-008: Snapshot 子配置片段组装

- **Given** Snapshot 中仅有 LLMSettings 和 RAGSettings（无 FullAgentConfig）
- **When** 调用 `LoadFromSnapshot(agentId)`
- **Then** 组装返回包含 Llm 和 Rag 的 AgentConfig

### TC-CM-009: ListAgentsAsync - Redis 不可用

- **Given** Redis `IsAvailable == false`
- **When** 调用 `ListAgentsAsync()`
- **Then** 返回空列表

### TC-CM-010: ListAgentsAsync - 正常返回

- **Given** Redis 中 `agent:published:index` 包含 agent-a
- **When** 调用 `ListAgentsAsync()`
- **Then** 返回包含 agent-a 的 AgentSummary 列表

### TC-CM-011: Redis 反序列化失败

- **Given** Redis 中 `agent:config:{agentId}` 存在但 JSON 格式错误
- **When** 调用 `GetConfigAsync(agentId)`
- **Then** 日志记录 Error，继续降级到 Mock 或返回 null

### TC-CM-012: ConfigSnapshot 并发读写

- **Given** 多线程同时读写同一 agentId 的配置
- **When** 并发调用 SetConfig 和 GetConfig
- **Then** 不抛出异常，数据一致
