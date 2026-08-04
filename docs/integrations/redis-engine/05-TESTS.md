# 05-TESTS — Redis 连接管理测试用例

> 关联文档：[02-SPEC.md](./02-SPEC.md) | [04-TASKS.md](./04-TASKS.md)

---

## 1. 现有测试

### 1.1 RedisHealthCheckTests

**文件路径：** `test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs`

#### TC-HC-01: Redis 不可用时返回 Degraded

- **Given** FakeRedisConnectionProvider 的 `IsAvailableValue = false`
- **When** 调用 `RedisHealthCheck.CheckHealthAsync()`
- **Then** 返回 `HealthCheckResult`，`Status == HealthStatus.Degraded`

```xunit
[Fact] public async Task Returns_degraded_when_redis_not_available()
```

#### TC-HC-02: Ping 成功时返回 Healthy

- **Given** FakeRedisConnectionProvider 的 `IsAvailableValue = true`，`PingAsync` 正常返回
- **When** 调用 `RedisHealthCheck.CheckHealthAsync()`
- **Then** 返回 `HealthCheckResult`，`Status == HealthStatus.Healthy`

```xunit
[Fact] public async Task Returns_healthy_when_ping_succeeds()
```

#### TC-HC-03: Ping 抛异常时返回 Unhealthy

- **Given** FakeRedisConnectionProvider 的 `IsAvailableValue = true`，`PingException = RedisConnectionException`
- **When** 调用 `RedisHealthCheck.CheckHealthAsync()`
- **Then** 返回 `HealthCheckResult`，`Status == HealthStatus.Unhealthy`，`Exception != null`

```xunit
[Fact] public async Task Returns_unhealthy_when_ping_throws()
```

---

### 1.2 HotReloadTests

**文件路径：** `test/OpenAgent.Engine.Tests/Config/HotReloadTests.cs`

#### TC-HR-01: Legacy Agent 频道消息刷新 Snapshot

- **Given** FakeRedisConnectionProvider 中存储了 `agent:config:agent-a` 的 JSON 配置
- **When** 调用 `ProcessMessage("agent:config:changed", "agent-a")`
- **Then** Snapshot 中 `FullAgentConfig` 的 `Llm.Provider == "provider-a"`

```xunit
[Fact] public void ProcessMessage_RefreshesSnapshotFromLegacyAgentChannel()
```

#### TC-HR-02: 结构化消息从当前频道触发全量刷新

- **Given** 空的 ConfigSnapshot 和 FakeRedisConnectionProvider
- **When** 调用 `ProcessMessage("agent:config:updates", structuredJson)`，JSON 包含 `configType: "LLMSettings"`
- **Then** 忽略 payload 子配置 data，从 Redis 全量刷新，`LLMSettings` 的 `Provider == "provider-b"`

```xunit
[Fact] public void ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis()
```

#### TC-HR-04: ConfigUpdate 类型触发从 Redis 全量刷新

- **Given** FakeRedisConnectionProvider 中存储了 `agent:config:agent-d` 的 JSON 配置
- **When** 收到 `type: "ConfigUpdate"` 的结构化消息
- **Then** 从 Redis 读取完整配置并更新 Snapshot，版本号正确

```xunit
[Fact] public void ProcessMessage_ConfigUpdate_RefreshesFullConfigFromRedis()
```

#### TC-HR-05: FullSync 清空快照

- **Given** Snapshot 中已存在 agent-full-sync 的完整配置
- **When** 收到 `type: "FullSync"` 消息
- **Then** 整个快照被清空

```xunit
[Fact] public void ProcessMessage_FullSync_ClearsSnapshot()
```

#### TC-HR-06: 空白消息被忽略

- **Given** 空的 ConfigSnapshot
- **When** 调用 `ProcessMessage(channel, "   ")`
- **Then** Snapshot 不受影响

```xunit
[Fact] public void ProcessMessage_IgnoresBlankPayload()
```

#### TC-HR-07: 无效 JSON 不覆盖已有 Snapshot

- **Given** Snapshot 中已存在 `agent-e` 的 `LLMSettings`，版本号 5
- **When** 调用 `ProcessMessage(channel, "{ invalid json")`
- **Then** Snapshot 中配置不变，版本号仍为 5

```xunit
[Fact] public void ProcessMessage_InvalidJson_DoesNotOverwriteExistingSnapshot()
```

#### TC-HR-08: Legacy 注册频道不修改 Snapshot

- **Given** Snapshot 中已存在 `agent-f` 的 `LLMSettings`，版本号 6
- **When** 调用 `ProcessMessage("skill:registry:changed", "agent-f")`
- **Then** Snapshot 中配置不变，版本号仍为 6

```xunit
[Fact] public void ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot()
```

---

### 1.3 ConfigProviderTests

**文件路径：** `test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs`

#### TC-CP-01: 无 agentId 调用抛 InvalidOperationException

- **Given** ConfigProvider 实例
- **When** 调用 `GetConfigAsync(CancellationToken.None)`（无 agentId）
- **Then** 抛出 `InvalidOperationException`

```xunit
[Fact] public async Task GetConfigAsync_without_agentId_throws()
```

#### TC-CP-02: 空 agentId + AllowMock=true 返回 Mock 配置

- **Given** `AllowMockAgent = true`
- **When** 调用 `GetConfigAsync("")`
- **Then** 返回 `FrameworkType == Mock` 的配置

```xunit
[Fact] public async Task GetConfigAsync_with_empty_agentId_returns_mock_when_allowed()
```

#### TC-CP-03: 空 agentId + AllowMock=false 返回 null

- **Given** `AllowMockAgent = false`
- **When** 调用 `GetConfigAsync("")`
- **Then** 返回 `null`

```xunit
[Fact] public async Task GetConfigAsync_with_empty_agentId_returns_null_when_not_allowed()
```

#### TC-CP-04: 从 Snapshot 加载配置

- **Given** Snapshot 中已存在 `agent-snap` 的完整配置
- **When** 调用 `GetConfigAsync("agent-snap")`
- **Then** 返回 Snapshot 中的配置，不从 Redis 读取

```xunit
[Fact] public async Task GetConfigAsync_loads_from_snapshot()
```

#### TC-CP-05: Snapshot 未命中时从 Redis 加载

- **Given** FakeRedisConnectionProvider 中存储了 `agent:config:agent-redis` 的 JSON 配置，Snapshot 为空
- **When** 调用 `GetConfigAsync("agent-redis")`
- **Then** 返回 Redis 中的配置，Snapshot 被缓存，版本号正确

```xunit
[Fact] public async Task GetConfigAsync_loads_from_redis_when_snapshot_miss()
```

#### TC-CP-06: 无配置 + AllowMock=true 返回 Mock 降级

- **Given** FakeRedisConnectionProvider 中无对应配置，`AllowMockAgent = true`
- **When** 调用 `GetConfigAsync("nonexistent-agent")`
- **Then** 返回 `FrameworkType == Mock` 的配置

```xunit
[Fact] public async Task GetConfigAsync_returns_mock_fallback_when_nothing_found_and_allowed()
```

#### TC-CP-07: 无配置 + AllowMock=false 返回 null

- **Given** FakeRedisConnectionProvider 中无对应配置，`AllowMockAgent = false`
- **When** 调用 `GetConfigAsync("nonexistent-agent")`
- **Then** 返回 `null`

```xunit
[Fact] public async Task GetConfigAsync_returns_null_when_nothing_found_and_not_allowed()
```

---

## 2. 缺失测试场景

### 2.1 RedisConnectionProvider — 孤岛模式

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-01 | 无连接字符串进入孤岛模式 | **Given** IConfiguration 中无 `ConnectionStrings:Redis`；**When** 创建 RedisConnectionProvider；**Then** `IsAvailable == false`，不抛异常 |
| MT-02 | 孤岛模式 StringGetAsync 返回 Null | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `StringGetAsync("any:key")`；**Then** 返回 `RedisValue.Null` |
| MT-03 | 孤岛模式 StringSetAsync 返回 false | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `StringSetAsync("any:key", "value")`；**Then** 返回 `false` |
| MT-04 | 孤岛模式 KeyDeleteAsync 返回 false | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `KeyDeleteAsync("any:key")`；**Then** 返回 `false` |
| MT-05 | 孤岛模式 SetMembersAsync 返回空数组 | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `SetMembersAsync("any:key")`；**Then** 返回空数组 |
| MT-06 | 孤岛模式 SetAddAsync 返回 false | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `SetAddAsync("any:key", "value")`；**Then** 返回 `false` |
| MT-07 | 孤岛模式 PingAsync 返回 Zero | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `PingAsync()`；**Then** 返回 `TimeSpan.Zero` |
| MT-08 | 孤岛模式 StringGet 返回 Null | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `StringGet("any:key")`；**Then** 返回 `RedisValue.Null` |
| MT-09 | 孤岛模式 GetDatabase 抛异常 | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `GetDatabase()`；**Then** 抛出 `InvalidOperationException` |
| MT-10 | 孤岛模式 GetServer 返回 null | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `GetServer()`；**Then** 返回 `null` |
| MT-11 | 孤岛模式 Subscribe 为空操作 | **Given** RedisConnectionProvider 处于孤岛模式；**When** 调用 `Subscribe(channel, handler)`；**Then** 不抛异常 |

### 2.2 FakeRedisConnectionProvider（测试替身）

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-20 | FakeRedis 内存存储 | **Given** FakeRedisConnectionProvider 实例；**When** 调用 `StringSetAsync(key, value)`；**Then** 值存储在内存字典中 |
| MT-21 | FakeRedis 读取已存储的值 | **Given** 已存储的 key-value；**When** 调用 `StringGetAsync(key)`；**Then** 返回存储的值 |
| MT-22 | FakeRedis 不存在的 key | **Given** 空的 FakeRedisConnectionProvider；**When** 调用 `StringGetAsync(key)`；**Then** 返回 `RedisValue.Null` |

### 2.5 DI 注册

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-25 | Core 的 IConnectionMultiplexer 注册 RedisConnectionProvider | **Given** `ConnectionStrings:Redis = "localhost:6379"`；**When** 调用 `AddAgentCore` + `AddAgentEngine`；**Then** `IRedisConnectionProvider` 解析为 `RedisConnectionProvider` 实例 |
| MT-26 | 测试中使用 FakeRedisConnectionProvider | **Given** 测试 DI 容器；**When** 注册 `FakeRedisConnectionProvider` 为 `IRedisConnectionProvider`；**Then** 所有 Redis 操作使用内存 Mock |

### 2.7 ConfigProvider — Redis 不可用

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-27 | Redis 不可用时跳过 Redis 查找 | **Given** `IsAvailable == false`；**When** 调用 `GetConfigAsync("agent-x")`；**Then** 不尝试从 Redis 读取，返回 null 或 Mock 降级 |
| MT-28 | ListAgentsAsync 在 Redis 不可用时返回空列表 | **Given** `IsAvailable == false`；**When** 调用 `ListAgentsAsync()`；**Then** 返回空列表 |

### 2.8 RedisRegistry

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-29 | RegisterAsync 成功注册 | **Given** Redis 可用；**When** 调用 `RegisterAsync()`；**Then** `IsRegistered == true`，Redis 中存在 `engine:registry:{id}` 键 |
| MT-30 | RegisterAsync 失败时 IsRegistered 为 false | **Given** Redis 不可用（孤岛模式）；**When** 调用 `RegisterAsync()`；**Then** `IsRegistered == false`，不抛异常 |
| MT-31 | DeregisterAsync 删除注册键 | **Given** Engine 已注册；**When** 调用 `DeregisterAsync()`；**Then** `IsRegistered == false`，Redis 中键被删除 |

### 2.9 Registrar 系列

| 编号 | 场景 | Given-When-Then |
|------|------|-----------------|
| MT-32 | RedisSkillRegistrar 在 Redis 不可用时跳过 | **Given** `IsAvailable == false`；**When** 调用 `StartAsync()`；**Then** 不尝试读取 Redis，直接返回 |
| MT-33 | RedisLlmRegistrar 在 Redis 不可用时跳过 | **Given** `IsAvailable == false`；**When** 调用 `StartAsync()`；**Then** 不尝试读取 Redis，直接返回 |
| MT-34 | RedisRagRegistrar 在 Redis 不可用时跳过 | **Given** `IsAvailable == false`；**When** 调用 `StartAsync()`；**Then** 不尝试读取 Redis，直接返回 |

---

## 3. 测试替身

### 3.1 FakeRedisConnectionProvider（TestDoubles）

**文件路径：** `test/OpenAgent.Engine.Tests/TestDoubles/FakeRedisConnectionProvider.cs`

- 基于 `Dictionary<string, string>` 的内存实现
- `IsAvailable` 固定返回 `true`
- `SetMembersAsync` / `SetAddAsync` 返回固定值（空数组 / true）
- `Subscribe` 为空操作
- 提供 `SetString(key, value)` 辅助方法用于测试设置

### 3.2 内联 Fake（RedisHealthCheckTests）

**文件路径：** `test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs`

- 私有 sealed 类，支持 `IsAvailableValue` 和 `PingException` 属性
- 用于精确控制健康检查的输入条件

### 3.3 内联 Fake（ConfigProviderTests）

**文件路径：** `test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs`

- 私有 sealed 类，支持 `SetString` 辅助方法
- `StringGetAsync` 从 Dictionary 读取
