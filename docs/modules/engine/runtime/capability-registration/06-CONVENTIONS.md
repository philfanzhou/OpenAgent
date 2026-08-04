# CapabilityRegistration - 编码约定

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
