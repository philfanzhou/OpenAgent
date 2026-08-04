# CapabilityRegistration - 测试文档

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
