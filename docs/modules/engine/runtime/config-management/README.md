
## Feature


## 核心用户故事

作为 Engine 运维人员，我希望 Engine 能从 Redis 加载 Agent 配置并缓存在内存中，以便请求执行时能快速获取正确的 Agent 设置。

## 功能简介

ConfigManagement 负责 Agent 配置的加载、缓存与提供。采用三级读取链（内存快照 → Redis → Mock 降级），确保在各种可用性场景下都能返回配置。通过 `IConfigSnapshot` 实现内存缓存，`ConfigProvider` 实现读取链与降级逻辑，`EnrichWithSecureSecrets` 从环境变量注入敏感信息。

## 关键能力

- **三级读取链**：Snapshot → Redis → Mock 降级，逐级回退
- **内存缓存**：ConfigSnapshot 基于 IMemoryCache，支持按 key 或 agentId+configType 存取
- **全量配置写入**：`SetFullConfig` 一次性写入所有子配置片段及版本号
- **敏感信息注入**：从环境变量 `LLM__APIKEY` / `LLM_API_KEY` 填充 LLM API Key
- **Mock 降级**：开发/测试环境自动启用 Mock Agent，无需真实配置
- **Agent 列表查询**：从 `agent:published:index` 获取已发布 Agent 列表
 - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定

## Specification


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

## Design


## 架构概览

```
┌──────────────┐    GetConfigAsync(agentId)    ┌──────────────┐
│ ConfigProvider │ ─────────────────────────→ │ ConfigSnapshot │
│ (IAgentConfigProvider) │     1. Snapshot     │ (IMemoryCache) │
└──────────────┘                              └──────────────┘
       │ 2. Redis                                         ↑
       ↓                                                  │ SetFullConfig
┌──────────────┐                                          │
│    Redis     │ ──── agent:config:{agentId} ─────────────┘
│              │      agent:published:index
└──────────────┘
       │ 3. Mock fallback
       ↓
┌──────────────┐
│ Mock Config  │  FrameworkType=Mock, Rag.Enabled=false
└──────────────┘
```

## 文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/Engine/Abstractions/IConfigSnapshot.cs` | 配置快照接口定义 |
| `src/Engine/Models/ConfigSnapshot.cs` | 配置快照实现（基于 IMemoryCache） |
| `src/Engine/Config/ConfigProvider.cs` | 配置提供者（三级读取链） |
| `src/Engine/Extensions/ServiceCollectionExtensions.cs` | DI 注册 |

## 接口定义

### IConfigSnapshot

```csharp
public interface IConfigSnapshot
{
    T? GetConfig<T>(string key);
    void SetConfig<T>(string key, T value);
    bool TryGetConfig<T>(string key, out T? config);
    T? GetConfig<T>(string agentId, string configType);
    void SetConfig<T>(string agentId, string configType, T value);
    bool TryGetConfig<T>(string agentId, string configType, out T? config);
    long? GetVersion(string key);
    void SetVersion(string key, long value);
    long? GetVersion(string agentId, string configType);
    void SetVersion(string agentId, string configType, long value);
}
```

### IAgentConfigProvider（来自 Contracts）

```csharp
// 外部接口，ConfigProvider 实现
Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default);
Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default);
Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default);
```

## 数据依赖

### Redis 数据结构

| Key 模式 | 类型 | 值 | 说明 |
|---------|------|-----|------|
| `agent:config:{agentId}` | String | AgentConfigEntity JSON | Agent 完整配置 |
| `agent:published:index` | Set | agentId 集合 | 已发布 Agent 索引 |

### 内存缓存 Key 格式

| Key 模式 | 说明 |
|---------|------|
| `agent:{agentId}:config:{configType}` | 配置缓存（configType: FullAgentConfig/LLMSettings/RAGSettings/MCPSettings/SkillsSettings） |
| `agent:{agentId}:config:{configType}:version` | 版本号缓存 |

### ConfigType 枚举值

- `FullAgentConfig` - 完整 Agent 配置
- `LLMSettings` - LLM 配置
- `RAGSettings` - RAG 配置
- `MCPSettings` - MCP 配置
- `SkillsSettings` - Skills 配置

## DI 注册

```csharp
// ServiceCollectionExtensions.cs
services.AddSingleton<ConfigSnapshot>();
services.AddSingleton<IAgentConfigProvider, ConfigProvider>();
```

注意：`ConfigSnapshot` 注册为 Singleton 但未通过接口注册，因为 `HotReloadService` 等内部类直接依赖具体类。

## 读取链详细流程

```
GetConfigAsync(agentId)
  │
  ├─ agentId 为空?
  │   ├─ AllowMockAgent=true → 返回 Mock 配置
  │   └─ AllowMockAgent=false → 返回 null
  │
  ├─ LoadFromSnapshot(agentId)
  │   ├─ TryGetConfig FullAgentConfig → 命中 → EnrichWithSecureSecrets → 返回
  │   └─ 尝试组装各子配置片段 → 有任一 → EnrichWithSecureSecrets → 返回
  │
  ├─ Redis.IsAvailable?
  │   ├─ true → LoadFromRedisAsync(agentId)
  │   │   ├─ StringGetAsync agent:config:{agentId}
  │   │   ├─ 反序列化 AgentConfigEntity
  │   │   ├─ SetFullConfig 写入 Snapshot
  │   │   └─ 返回 config
  │   └─ false → 日志 "Entering island mode"
  │
  ├─ AllowMockAgent=true → 返回 Mock 配置
  │
  └─ 返回 null
```

## 关键设计决策

1. **ConfigSnapshot 为 internal 具体类**：虽然实现了 IConfigSnapshot 接口，但 DI 注册时直接注册具体类，因为 HotReloadService 等内部组件需要直接访问
2. **lock 保护写入**：IMemoryCache 本身线程安全，但 SetConfig 使用 `lock` 确保同一 key 的写入串行化
3. **EnrichWithSecureSecrets 在读取时调用**：每次从 Snapshot 读取配置时都注入环境变量中的密钥，确保环境变量更新后能生效
4. **版本号与配置分离存储**：版本号使用独立的 cache key，支持版本比较防止热加载时的旧数据覆盖
5. **GetVersion 返回 `long?` 但缺失 key 时返回 0**：`GetVersion` 返回类型为 `long?`，但 `TryGetValue` 失败时 `out` 为 `long` 默认值 0，因此缺失 key 时实际返回 `(long?)0` 而非 `null`

## Tasks


```json
[
  {
    "id": "CM-001",
    "title": "定义 IConfigSnapshot 接口",
    "description": "定义 GetConfig/SetConfig/TryGetConfig（按 key 和 agentId+configType）及 GetVersion/SetVersion 方法",
    "status": "implemented",
    "file": "src/Engine/Abstractions/IConfigSnapshot.cs"
  },
  {
    "id": "CM-002",
    "title": "实现 ConfigSnapshot 内存缓存",
    "description": "基于 IMemoryCache 实现，lock 保护写入，支持 BuildCacheKey/BuildVersionKey 格式化",
    "status": "implemented",
    "file": "src/Engine/Models/ConfigSnapshot.cs"
  },
  {
    "id": "CM-003",
    "title": "实现 SetFullConfig 批量写入",
    "description": "一次性写入 FullAgentConfig/LLMSettings/RAGSettings/MCPSettings/SkillsSettings 及版本号",
    "status": "implemented",
    "file": "src/Engine/Models/ConfigSnapshot.cs"
  },
  {
    "id": "CM-004",
    "title": "实现 ConfigProvider 三级读取链",
    "description": "Snapshot → Redis → Mock 降级，含 EnrichWithSecureSecrets 和 AllowMockAgent 解析",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-005",
    "title": "实现无 agentId 调用抛异常",
    "description": "GetConfigAsync() 无 agentId 重载始终抛出 InvalidOperationException",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-006",
    "title": "实现敏感信息注入",
    "description": "EnrichWithSecureSecrets 从 LLM__APIKEY/LLM_API_KEY 环境变量注入 API Key",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-007",
    "title": "实现 AllowMockAgent 解析",
    "description": "优先级：配置值 > 环境变量 > IsDevelopment/IsTesting",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-008",
    "title": "实现 ListAgentsAsync",
    "description": "从 agent:published:index 读取已发布 Agent 列表并返回 AgentSummary",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-009",
    "title": "DI 注册",
    "description": "ConfigSnapshot 注册为 Singleton，ConfigProvider 注册为 IAgentConfigProvider",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "CM-010",
    "title": "编写 ConfigProviderTests",
    "description": "测试无 agentId 异常、Snapshot 命中、Redis 读取、Mock 降级等场景",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs"
  },
  {
    "id": "CM-011",
    "title": "编写 ConfigSnapshotTests",
    "description": "测试 SetConfig/GetConfig 往返、TryGetConfig 缺失、SetFullConfig 全量写入、版本管理",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/Config/ConfigSnapshotTests.cs"
  }
]
```

## Tests


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

## Conventions


## 命名约定

### 接口命名

- 配置快照接口：`IConfigSnapshot`
- 配置提供者接口（外部）：`IAgentConfigProvider`（来自 Contracts 包）

### 类命名

- 快照实现类：`ConfigSnapshot`（internal，无接口前缀）
- 配置提供者：`ConfigProvider`（internal，实现外部接口）

### 方法命名

- 读取方法：`GetConfig<T>`、`GetVersion`
- 写入方法：`SetConfig<T>`、`SetVersion`
- 尝试读取：`TryGetConfig<T>`
- 批量写入：`SetFullConfig`
- 降级配置：`CreateMockFallbackConfig`
- 密钥注入：`EnrichWithSecureSecrets`
- 解析方法：`ResolveAllowMockAgent`、`ParseVersion`
- Redis 读取：`LoadFromRedisAsync`
- Snapshot 读取：`LoadFromSnapshot`

### 私有方法命名

- 辅助方法使用 `PascalCase`：`BuildCacheKey`、`BuildVersionKey`
- 转换方法使用 `To` 前缀：无此场景（ConfigProvider 不做类型转换）

### 字段命名

- 私有字段使用 `_` 前缀 + camelCase：`_cache`、`_lock`、`_redis`、`_environment`、`_configuration`、`_logger`、`_snapshot`、`_allowMockAgent`
- 静态只读字段使用 `PascalCase`：`CaseInsensitiveJsonOptions`

## 日志约定

### 日志级别

| 场景 | 级别 | 示例 |
|------|------|------|
| 从 Redis 加载配置 | Information | `"Agent config loaded from Redis and cached for agent {AgentId}"` |
| Mock 降级 | Information | `"No AgentId provided. Degrading to MockAgent (AllowMockAgent=true)."` |
| Snapshot 命中 | Debug | `"Agent config loaded from in-memory snapshot for agent {AgentId}"` |
| Redis 不可用 | Warning | `"Redis is not available. Entering island mode — skipping Redis config lookup for agent {AgentId}."` |
| 无配置 | Warning | `"No cached configuration available for agent {AgentId}."` |
| 反序列化失败 | Error | `"Failed to deserialize agent config from Redis for agent: {AgentId}"` |

### 结构化日志参数

- 使用 `{AgentId}`、`{Version}`、`{FrameworkType}` 等命名参数
- 日志消息为完整英文句子

## 错误处理约定

### 异常策略

- **无 agentId 调用**：抛出 `InvalidOperationException`，明确告知调用方使用正确的重载
- **Redis 异常**：不向上抛出，降级到下一级读取链
- **反序列化失败**：记录 Error 日志，返回 null 继续降级

### 降级策略

```
Snapshot → Redis → Mock → null
```

每级失败后静默降级，不抛出异常。

## 缓存 Key 命名约定

- 配置 key：`agent:{agentId}:config:{configType}`
- 版本 key：`agent:{agentId}:config:{configType}:version`
- 使用冒号 `:` 作为层级分隔符

## JSON 序列化约定

- 使用 `System.Text.Json`
- `PropertyNameCaseInsensitive = true`：忽略属性名大小写
- `JsonStringEnumConverter`：枚举序列化为字符串
- 静态共享 `JsonSerializerOptions` 实例

## DI 注册约定

- `ConfigSnapshot` 注册为 Singleton（具体类，非接口）
- `ConfigProvider` 注册为 `IAgentConfigProvider` 的 Singleton
- `ConfigProvider` 构造函数直接依赖 `ConfigSnapshot` 具体类（非接口）

## 环境变量约定

- 双下划线 `__` 格式：`LLM__APIKEY`（.NET 配置层级标准）
- 下划线 `_` 格式：`LLM_API_KEY`（兼容格式）
- 全大写：`ALLOW_MOCK_AGENT`
- 优先级：`__` 格式 > `_` 格式

## 线程安全约定

- `ConfigSnapshot.SetConfig` / `SetVersion` 使用 `lock(_lock)` 保护写入
- `ConfigSnapshot.GetConfig` / `GetVersion` 无锁读取（IMemoryCache.TryGetValue 线程安全）
- `ConfigProvider` 无状态，线程安全
