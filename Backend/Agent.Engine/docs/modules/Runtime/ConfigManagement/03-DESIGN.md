# ConfigManagement - 设计文档

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
