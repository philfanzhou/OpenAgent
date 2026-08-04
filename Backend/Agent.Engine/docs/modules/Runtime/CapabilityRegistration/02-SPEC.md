# CapabilityRegistration - 功能规格说明

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
