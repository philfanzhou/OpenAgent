# CapabilityRegistration - 设计文档

## 架构概览

```
┌──────────────────────┐   StartAsync   ┌─────────┐
│ RedisLlmRegistrar    │ ─────────────→ │  Redis  │
│ (IHostedService)     │ ←───────────── │         │
│                      │   LlmProvider  │         │
│                      │   Profile[]    │         │
└──────────────────────┘                └─────────┘
         │
         ↓ Register()
┌──────────────────────┐
│ ILlmRegistry         │
└──────────────────────┘

┌──────────────────────┐   StartAsync   ┌─────────┐
│ RedisRagRegistrar    │ ─────────────→ │  Redis  │
│ (IHostedService)     │ ←───────────── │         │
│                      │   RagInstance  │         │
│                      │   Config[]     │         │
└──────────────────────┘                └─────────┘
         │
         ↓ Register()
┌──────────────────────┐
│ IRagRegistry         │
└──────────────────────┘

┌──────────────────────┐   StartAsync   ┌─────────┐
│ RedisSkillRegistrar  │ ─────────────→ │  Redis  │
│ (IHostedService)     │ ←───────────── │         │
│                      │   SkillInstance│         │
│                      │   Config[]     │         │
└──────────────────────┘                └─────────┘
         │
         ↓ RegisterTool()
┌──────────────────────┐
│ IToolRegistry        │
│                      │
│  ┌─────────────────┐ │
│  │ RedisMockSkill  │ │ ← ExecuteAsync
│  │ (inner class)   │ │
│  │  ↓ HttpEndpoint │ │
│  │  HttpClient POST│ │
│  └─────────────────┘ │
└──────────────────────┘
```

## 文件清单

| 文件路径 | 职责 |
|---------|------|
| `src/Engine/Redis/RedisLlmRegistrar.cs` | LLM Provider 注册器 |
| `src/Engine/Redis/RedisRagRegistrar.cs` | RAG Instance 注册器 |
| `src/Engine/Redis/RedisSkillRegistrar.cs` | Skill 注册器（含 RedisMockSkill） |
| `src/Engine/Extensions/ServiceCollectionExtensions.cs` | DI 注册 |

## 类定义

### RedisLlmRegistrar

```csharp
internal class RedisLlmRegistrar : IHostedService
{
    private readonly IRedisConnectionProvider _redis;
    private readonly ILlmRegistry _llmRegistry;
    private readonly ILogger<RedisLlmRegistrar> _logger;

    public Task StartAsync(CancellationToken cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

### RedisRagRegistrar

```csharp
internal class RedisRagRegistrar : IHostedService
{
    private readonly IRedisConnectionProvider _redis;
    private readonly IRagRegistry _ragRegistry;
    private readonly ILogger<RedisRagRegistrar> _logger;

    public Task StartAsync(CancellationToken cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

### RedisSkillRegistrar

```csharp
internal class RedisSkillRegistrar : IHostedService
{
    private readonly IRedisConnectionProvider _redis;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<RedisSkillRegistrar> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public Task StartAsync(CancellationToken cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class RedisMockSkill
    {
        private readonly SkillInstanceConfig _metadata;
        private readonly IHttpClientFactory _httpClientFactory;

        public string Name { get; }
        public string Description { get; }
        public Task<string> ExecuteAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken);
    }
}
```

## 数据依赖

### Redis 数据结构

| Key 模式 | 类型 | Registrar | 值类型 |
|---------|------|-----------|--------|
| `llm:published:index` | Set | RedisLlmRegistrar | profileId 集合 |
| `llm:registry:{profileId}` | String | RedisLlmRegistrar | LlmProviderProfile JSON |
| `rag:published:index` | Set | RedisRagRegistrar | instanceId 集合 |
| `rag:registry:{instanceId}` | String | RedisRagRegistrar | RagInstanceConfig JSON |
| `skill:published:index` | Set | RedisSkillRegistrar | skillName 集合 |
| `skill:registry:{skillName}` | String | RedisSkillRegistrar | SkillInstanceConfig JSON |

### 注册目标接口

| 接口 | 方法 | Registrar |
|------|------|-----------|
| `ILlmRegistry` | `Register(LlmProviderProfile profile)` | RedisLlmRegistrar |
| `IRagRegistry` | `Register(RagInstanceConfig config)` | RedisRagRegistrar |
| `IToolRegistry` | `RegisterTool(ToolDefinition, Func<...>)` | RedisSkillRegistrar |

## DI 注册

```csharp
// ServiceCollectionExtensions.cs
services.AddHostedService<RedisSkillRegistrar>();
services.AddHostedService<RedisRagRegistrar>();
services.AddHostedService<RedisLlmRegistrar>();
```

## 注册流程（三个 Registrar 统一模式）

```
StartAsync(cancellationToken)
  │
  ├─ _redis.IsAvailable? ── 否 → LogDebug("Redis not available. Skipping...") → return
  │
  ├─ 读取 {domain}:published:index
  │   ├─ 异常 → LogWarning → return
  │   └─ 为空 → LogInformation("No {domain} found...") → return
  │
  ├─ 遍历每个 ID:
  │   ├─ StringGet {domain}:registry:{id}
  │   │   └─ IsNullOrEmpty → continue
  │   ├─ Deserialize<{ConfigType}>(json)
  │   │   ├─ null 或 ID 为空 → continue
  │   │   └─ 异常 → LogError → continue
  │   └─ Registry.Register(config)
  │       └─ registered++
  │
  └─ LogInformation("{Count} {domain} registered from Redis.")
```

## RedisMockSkill 执行流程

```
ExecuteAsync(arguments, cancellationToken)
  │
  ├─ Type == "HttpEndpoint" && EndpointUrl 非空?
  │   │
  │   ├─ 是 → ExecuteHttpEndpointAsync
  │   │   ├─ JsonSerializer.Serialize(arguments)
  │   │   ├─ HttpClient.PostAsync(EndpointUrl, content, cancellationToken)
  │   │   ├─ IsSuccessStatusCode → 返回 responseBody
  │   │   ├─ 非 2xx → 返回 "Skill endpoint returned error: {StatusCode} - {ResponseBody}"
  │   │   └─ 异常 → 返回 "Skill endpoint call failed: {ex.Message}"
  │   │
  │   └─ 否 → 返回 "Skill '{Name}' is not configured with a valid endpoint..."
  │
```

## 关键设计决策

1. **同步 Redis 读取**：使用 `.GetAwaiter().GetResult()` 而非 `await`，因为 `StartAsync` 需要在服务启动前完成注册，且 IHostedService 的 StartAsync 支持同步执行
2. **IHttpClientFactory 命名客户端**：RedisMockSkill 通过 `IHttpClientFactory.CreateClient("SkillEndpoint")` 创建客户端，继承 Core 的 skip-cert handler 并尊重 DNS 刷新，30s 超时保留
3. **RedisMockSkill 作为内部类**：封装在 RedisSkillRegistrar 内，不对外暴露
4. **错误返回字符串而非异常**：RedisMockSkill.ExecuteAsync 返回错误消息字符串，而非抛出异常，简化调用方处理
5. **三个 Registrar 结构一致**：遵循相同的"读取索引→遍历加载→注册"模式，便于维护
6. **LlmRegistrar 使用 JsonStringEnumConverter**：LLM 配置中包含枚举类型，需要枚举转字符串的转换器
