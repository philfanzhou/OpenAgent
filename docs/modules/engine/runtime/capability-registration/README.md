
## Feature


## 核心用户故事

作为 Engine 运维人员，我希望 Engine 启动时能从 Redis 加载 LLM/RAG/Skill 能力配置，以便这些能力在 Agent 执行时可用。

## 功能简介

CapabilityRegistration 在 Engine 启动时从 Redis 加载三类能力配置：LLM Provider Profile、RAG Instance Config 和 Skill Instance Config。三个 Registrar 均实现 IHostedService，在 StartAsync 中同步读取 Redis 索引和详细配置，通过各自的 Registry 注册到内存中。RedisSkillRegistrar 创建 RedisMockSkill 代理，支持 HttpEndpoint 类型 Skill 的远程调用。

## 关键能力

- **LLM 能力注册**：从 `llm:published:index` 加载 LlmProviderProfile
- **RAG 能力注册**：从 `rag:published:index` 加载 RagInstanceConfig
- **Skill 能力注册**：从 `skill:published:index` 加载 SkillInstanceConfig，创建 RedisMockSkill 代理
- **HttpEndpoint Skill 执行**：RedisMockSkill 对 HttpEndpoint 类型 Skill 通过 HTTP POST 调用远程端点
- **Redis 不可用跳过**：所有 Registrar 在 Redis 不可用时静默跳过
 - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定

## Specification


## 功能需求 (FR)

### FR-CR-001: LLM 能力注册

- **类**: `RedisLlmRegistrar : IHostedService`
- **依赖**: `IRedisConnectionProvider`、`ILlmRegistry`、`ILogger<RedisLlmRegistrar>`
- **StartAsync 行为**:
  1. 检查 `_redis.IsAvailable`，不可用则跳过
  2. 读取 `llm:published:index` Redis Set 获取 profileId 列表
  3. 对每个 profileId，读取 `llm:registry:{profileId}` 反序列化为 `LlmProviderProfile`
  4. 调用 `_llmRegistry.Register(profile)` 注册
- **StopAsync**: 返回 `Task.CompletedTask`（无操作）

### FR-CR-002: RAG 能力注册

- **类**: `RedisRagRegistrar : IHostedService`
- **依赖**: `IRedisConnectionProvider`、`IRagRegistry`、`ILogger<RedisRagRegistrar>`
- **StartAsync 行为**:
  1. 检查 `_redis.IsAvailable`，不可用则跳过
  2. 读取 `rag:published:index` Redis Set 获取 instanceId 列表
  3. 对每个 instanceId，读取 `rag:registry:{instanceId}` 反序列化为 `RagInstanceConfig`
  4. 调用 `_ragRegistry.Register(config)` 注册
- **StopAsync**: 返回 `Task.CompletedTask`

### FR-CR-003: Skill 能力注册

- **类**: `RedisSkillRegistrar : IHostedService`
- **依赖**: `IRedisConnectionProvider`、`IToolRegistry`、`ILogger<RedisSkillRegistrar>`
- **StartAsync 行为**:
  1. 检查 `_redis.IsAvailable`，不可用则跳过
  2. 读取 `skill:published:index` Redis Set 获取 skillName 列表
  3. 对每个 skillName，读取 `skill:registry:{skillName}` 反序列化为 `SkillInstanceConfig`
  4. 创建 `RedisMockSkill(metadata)` 实例
  5. 调用 `_toolRegistry.RegisterTool(ToolDefinition, mockSkill.ExecuteAsync)` 注册
- **StopAsync**: 返回 `Task.CompletedTask`

### FR-CR-004: RedisMockSkill 执行

- **类**: `RedisSkillRegistrar.RedisMockSkill`（内部类）
- **HttpEndpoint 类型**:
  - POST JSON payload 到 `EndpointUrl`
  - 超时 30 秒
  - 成功：返回响应体
  - HTTP 错误：返回 `"Skill endpoint returned error: {StatusCode} - {ResponseBody}"`
  - 异常：返回 `"Skill endpoint call failed: {ex.Message}"`
- **其他类型**: 返回 `"Skill '{Name}' is not configured with a valid endpoint. Type: {Type}, EndpointUrl: {EndpointUrl}"`
- **HttpClient**: 静态共享实例，超时 30 秒

### FR-CR-005: 同步 Redis 读取

- 所有三个 Registrar 在 `StartAsync` 中使用 `.GetAwaiter().GetResult()` 同步等待 Redis 操作
- 索引读取使用 `_redis.SetMembersAsync(...).GetAwaiter().GetResult()`
- 详细配置读取使用 `_redis.StringGet(...)`

## 验收标准 (AC)

### AC-CR-001: LLM Profile 注册成功 [当前无测试覆盖]

- **Given** Redis 可用，`llm:published:index` 包含 profile-1
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** `ILlmRegistry.Register()` 被调用，profile-1 注册成功

### AC-CR-002: LLM Profile 反序列化失败跳过 [当前无测试覆盖]

- **Given** Redis 可用，`llm:registry:bad-profile` JSON 格式错误
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** 记录 Error 日志，跳过该 profile，继续处理其他

### AC-CR-003: RAG Instance 注册成功 [当前无测试覆盖]

- **Given** Redis 可用，`rag:published:index` 包含 rag-1
- **When** `RedisRagRegistrar.StartAsync` 执行
- **Then** `IRagRegistry.Register()` 被调用，rag-1 注册成功

### AC-CR-004: Skill 注册成功 [当前无测试覆盖]

- **Given** Redis 可用，`skill:published:index` 包含 skill-1
- **When** `RedisSkillRegistrar.StartAsync` 执行
- **Then** `IToolRegistry.RegisterTool()` 被调用，skill-1 注册成功

### AC-CR-005: RedisMockSkill HttpEndpoint 执行 [当前无测试覆盖]

- **Given** Skill 类型为 HttpEndpoint，EndpointUrl 有效
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** POST JSON 到 EndpointUrl，返回响应体

### AC-CR-006: RedisMockSkill 非 HttpEndpoint 类型 [当前无测试覆盖]

- **Given** Skill 类型非 HttpEndpoint
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** 返回错误消息字符串

### AC-CR-007: Redis 不可用时跳过注册 [当前无测试覆盖]

- **Given** `IRedisConnectionProvider.IsAvailable == false`
- **When** 任意 Registrar 的 `StartAsync` 执行
- **Then** 记录 Debug 日志，跳过注册，不抛出异常

### AC-CR-008: 索引为空时正常退出 [当前无测试覆盖]

- **Given** Redis 可用，但 `llm:published:index` 为空
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** 记录 Information 日志 "No LLM profiles found"，正常退出

## Redis 数据结构

| Key 模式 | 类型 | 值 | Registrar |
|---------|------|-----|-----------|
| `llm:published:index` | Set | profileId 集合 | RedisLlmRegistrar |
| `llm:registry:{profileId}` | String | LlmProviderProfile JSON | RedisLlmRegistrar |
| `rag:published:index` | Set | instanceId 集合 | RedisRagRegistrar |
| `rag:registry:{instanceId}` | String | RagInstanceConfig JSON | RedisRagRegistrar |
| `skill:published:index` | Set | skillName 集合 | RedisSkillRegistrar |
| `skill:registry:{skillName}` | String | SkillInstanceConfig JSON | RedisSkillRegistrar |

## Design


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

## Tasks


```json
[
  {
    "id": "CR-001",
    "title": "实现 RedisLlmRegistrar",
    "description": "IHostedService：读取 llm:published:index，加载 LlmProviderProfile，调用 ILlmRegistry.Register",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisLlmRegistrar.cs"
  },
  {
    "id": "CR-002",
    "title": "实现 RedisRagRegistrar",
    "description": "IHostedService：读取 rag:published:index，加载 RagInstanceConfig，调用 IRagRegistry.Register",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisRagRegistrar.cs"
  },
  {
    "id": "CR-003",
    "title": "实现 RedisSkillRegistrar",
    "description": "IHostedService：读取 skill:published:index，加载 SkillInstanceConfig，创建 RedisMockSkill，调用 IToolRegistry.RegisterTool",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisSkillRegistrar.cs"
  },
  {
    "id": "CR-004",
    "title": "实现 RedisMockSkill HttpEndpoint 执行",
    "description": "内部类：HttpEndpoint 类型 POST JSON 到 EndpointUrl（30s 超时），其他类型返回错误字符串",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisSkillRegistrar.cs"
  },
  {
    "id": "CR-005",
    "title": "DI 注册三个 Registrar",
    "description": "注册 RedisSkillRegistrar、RedisRagRegistrar、RedisLlmRegistrar 为 HostedService",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "CR-006",
    "title": "编写 CapabilityRegistration 单元测试",
    "description": "覆盖 LLM/RAG/Skill 注册成功/失败、Redis 不可用跳过、RedisMockSkill 执行等场景",
    "status": "pending",
    "file": ""
  }
]
```

## Tests


## 现有测试

当前无针对 CapabilityRegistration 功能的专门测试文件。

## 缺失测试场景

### LLM 注册测试

### TC-CR-001: LLM Profile 注册成功

- **Given** Redis 可用，`llm:published:index` 包含 `profile-1`，`llm:registry:profile-1` 包含有效 LlmProviderProfile JSON
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** `ILlmRegistry.Register()` 被调用一次，参数为反序列化后的 LlmProviderProfile

### TC-CR-002: LLM 多个 Profile 注册

- **Given** `llm:published:index` 包含 `profile-1` 和 `profile-2`
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** `ILlmRegistry.Register()` 被调用两次

### TC-CR-003: LLM Profile 反序列化失败跳过

- **Given** `llm:registry:bad-profile` 包含无效 JSON
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** 记录 Error 日志，跳过该 profile，继续处理其他

### TC-CR-004: LLM Profile ID 为空跳过

- **Given** `llm:registry:empty-id` 反序列化后 `profile.Id` 为空
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** 跳过该 profile

### TC-CR-005: LLM 索引为空

- **Given** `llm:published:index` 为空
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** 记录 Information 日志 "No LLM profiles found"，不调用 Register

### TC-CR-006: LLM 读取索引异常

- **Given** `SetMembersAsync` 抛出异常
- **When** `RedisLlmRegistrar.StartAsync` 执行
- **Then** 记录 Warning 日志，不调用 Register

### RAG 注册测试

### TC-CR-007: RAG Instance 注册成功

- **Given** Redis 可用，`rag:published:index` 包含 `rag-1`，`rag:registry:rag-1` 包含有效 RagInstanceConfig JSON
- **When** `RedisRagRegistrar.StartAsync` 执行
- **Then** `IRagRegistry.Register()` 被调用一次

### TC-CR-008: RAG Instance 反序列化失败跳过

- **Given** `rag:registry:bad-rag` 包含无效 JSON
- **When** `RedisRagRegistrar.StartAsync` 执行
- **Then** 记录 Error 日志，跳过该 instance

### TC-CR-009: RAG Instance ID 为空跳过

- **Given** `rag:registry:empty-id` 反序列化后 `config.Id` 为空
- **When** `RedisRagRegistrar.StartAsync` 执行
- **Then** 跳过该 instance

### Skill 注册测试

### TC-CR-010: Skill 注册成功

- **Given** Redis 可用，`skill:published:index` 包含 `skill-1`，`skill:registry:skill-1` 包含有效 SkillInstanceConfig JSON
- **When** `RedisSkillRegistrar.StartAsync` 执行
- **Then** `IToolRegistry.RegisterTool()` 被调用，ToolDefinition.Name 等于 skill-1

### TC-CR-011: Skill Name 为空跳过

- **Given** `skill:registry:empty-name` 反序列化后 `metadata.Name` 为空
- **When** `RedisSkillRegistrar.StartAsync` 执行
- **Then** 跳过该 skill

### TC-CR-012: Skill ParametersJsonSchema 传递

- **Given** SkillInstanceConfig 的 ParametersJsonSchema 为 `{"type":"object"}`
- **When** `IToolRegistry.RegisterTool()` 被调用
- **Then** ToolDefinition.ParametersJsonSchema 等于 `{"type":"object"}`

### RedisMockSkill 测试

### TC-CR-013: HttpEndpoint 成功执行

- **Given** Skill 类型为 HttpEndpoint，EndpointUrl 为可访问的 HTTP 端点
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** POST JSON 到 EndpointUrl，返回响应体

### TC-CR-014: HttpEndpoint HTTP 错误

- **Given** Skill 类型为 HttpEndpoint，EndpointUrl 返回 500
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** 返回包含 "Skill endpoint returned error: 500" 的字符串

### TC-CR-015: HttpEndpoint 连接失败

- **Given** Skill 类型为 HttpEndpoint，EndpointUrl 不可达
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** 返回包含 "Skill endpoint call failed:" 的字符串

### TC-CR-016: 非 HttpEndpoint 类型

- **Given** Skill 类型为 "OtherType"
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** 返回包含 "not configured with a valid endpoint" 的字符串

### TC-CR-017: EndpointUrl 为空

- **Given** Skill 类型为 HttpEndpoint 但 EndpointUrl 为空
- **When** 调用 `ExecuteAsync(arguments, cancellationToken)`
- **Then** 返回包含 "not configured with a valid endpoint" 的字符串

### 通用测试

### TC-CR-018: Redis 不可用时跳过注册

- **Given** `IRedisConnectionProvider.IsAvailable == false`
- **When** 任意 Registrar 的 `StartAsync` 执行
- **Then** 记录 Debug 日志，跳过注册，不抛出异常

### TC-CR-019: StopAsync 无操作

- **Given** 任意 Registrar
- **When** 调用 `StopAsync(CancellationToken.None)`
- **Then** 返回已完成的 Task，无副作用

## 测试基础设施需求

- 需要 Mock `ILlmRegistry`、`IRagRegistry`、`IToolRegistry` 接口
- 需要 `FakeRedisConnectionProvider` 支持 `SetMembersAsync` 返回预设值
- 需要 `FakeRedisConnectionProvider` 支持 `StringGet` 返回预设 JSON
- RedisMockSkill 测试需要 HTTP 服务器 Mock（如 `HttpMessageHandler` mock）

## Conventions


## 命名约定

### 类命名

- Redis 注册器前缀 `Redis` + 领域名 + 后缀 `Registrar`：`RedisLlmRegistrar`、`RedisRagRegistrar`、`RedisSkillRegistrar`
- 内部代理类前缀 `Redis` + `Mock` + 领域名：`RedisMockSkill`

### 方法命名

- 加载方法：`LoadLlmProfilesFromRedis`、`LoadRagInstancesFromRedis`、`LoadSkillsFromRedis`
- 执行方法：`ExecuteAsync`、`ExecuteHttpEndpointAsync`

### 字段命名

- 私有字段使用 `_` 前缀 + camelCase：`_redis`、`_llmRegistry`/`_ragRegistry`/`_toolRegistry`、`_logger`、`_metadata`
- 静态字段使用 `PascalCase`：`JsonOptions`；静态私有字段使用 `_` 前缀 + camelCase：`_httpClient`

## 日志约定

### 日志级别

| 场景 | 级别 | 示例 |
|------|------|------|
| Redis 不可用跳过 | Debug | `"RedisLlmRegistrar: Redis not available. Skipping LLM profile registration."` |
| 单个注册成功 | Debug | `"RedisLlmRegistrar: Registered LLM profile '{Id}' from Redis"` |
| 批量注册完成 | Information | `"RedisLlmRegistrar: {Count} LLM profiles registered from Redis."` |
| 索引为空 | Information | `"RedisLlmRegistrar: No LLM profiles found in published index."` |
| 读取索引失败 | Warning | `"RedisLlmRegistrar: Failed to read llm:published:index from Redis"` |
| 单个注册失败 | Error | `"RedisLlmRegistrar: Failed to register LLM profile '{Id}'"` |

### 日志前缀约定

- 日志消息以类名开头：`"RedisLlmRegistrar:"`、`"RedisRagRegistrar:"`、`"RedisSkillRegistrar:"`
- 便于在日志中快速定位来源

## 错误处理约定

### 异常策略

- **索引读取异常**：捕获异常，记录 Warning 日志，直接返回（不继续处理）
- **单个配置反序列化/注册异常**：捕获异常，记录 Error 日志，跳过该项继续处理
- **RedisMockSkill 执行异常**：捕获异常，返回错误消息字符串（不抛出）

### 降级策略

- Redis 不可用 → 静默跳过（Debug 日志）
- 索引读取失败 → 静默返回（Warning 日志）
- 单个配置失败 → 跳过该项（Error 日志），继续处理其他

## 同步 Redis 读取约定

- `StartAsync` 中使用 `.GetAwaiter().GetResult()` 同步等待异步 Redis 操作
- 索引读取：`_redis.SetMembersAsync(key).GetAwaiter().GetResult()`
- 详细配置读取：`_redis.StringGet(key)`（同步方法）
- 原因：IHostedService.StartAsync 需要在服务就绪前完成注册

## JSON 序列化约定

### JsonSerializerOptions

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }  // 仅 LlmRegistrar
};
```

- `PropertyNameCaseInsensitive = true`：忽略属性名大小写
- LlmRegistrar 额外添加 `JsonStringEnumConverter`：LLM 配置含枚举类型
- RAG 和 Skill Registrar 不使用 `JsonStringEnumConverter`

## HttpClient 约定

- 使用静态共享实例：`private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) }`
- 超时 30 秒
- 不使用 `IHttpClientFactory`（因为是内部类，无法注入）
- 所有 Skill 调用共享同一 HttpClient 实例

## ToolDefinition 构建约定

```csharp
new ToolDefinition
{
    Name = mockSkill.Name,
    Description = mockSkill.Description,
    ParametersJsonSchema = metadata.ParametersJsonSchema ?? string.Empty
}
```

- Name 和 Description 来自 RedisMockSkill（从 SkillInstanceConfig 读取）
- ParametersJsonSchema 默认为空字符串（null 合并）

## IHostedService 实现约定

- `StartAsync`：执行注册逻辑
- `StopAsync`：返回 `Task.CompletedTask`（无操作）
- 注册为 `AddHostedService<T>()`
- 不实现 `IDisposable`（无需要释放的资源）
