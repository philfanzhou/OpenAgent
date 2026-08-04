# 02-SPEC — Redis 连接管理功能规格

> 关联文档：[01-FEATURE.md](./01-FEATURE.md) | [03-DESIGN.md](./03-DESIGN.md) | [05-TESTS.md](./05-TESTS.md)

---

## 1. 功能需求（FR）

### FR-01: IRedisConnectionProvider 接口定义

| 编号 | 需求 | 来源签名 |
|------|------|----------|
| FR-01.1 | 暴露 `bool IsAvailable` 属性，指示 Redis 连接是否可用 | `IRedisConnectionProvider.IsAvailable` |
| FR-01.2 | 提供 `IServer? GetServer(int database = 0)` 方法获取 Redis 服务器实例 | `IRedisConnectionProvider.GetServer` |
| FR-01.3 | 提供 `IDatabase GetDatabase(int database = 0)` 方法获取数据库实例 | `IRedisConnectionProvider.GetDatabase` |
| FR-01.4 | 提供 `Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags)` 异步字符串读取 | `IRedisConnectionProvider.StringGetAsync` |
| FR-01.5 | 提供 `Task<bool> StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry, CommandFlags flags)` 异步字符串写入（支持 TTL） | `IRedisConnectionProvider.StringSetAsync` |
| FR-01.6 | 提供 `Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags)` 异步键删除 | `IRedisConnectionProvider.KeyDeleteAsync` |
| FR-01.7 | 提供 `Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags)` 异步集合成员查询 | `IRedisConnectionProvider.SetMembersAsync` |
| FR-01.8 | 提供 `Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags)` 异步集合添加 | `IRedisConnectionProvider.SetAddAsync` |
| FR-01.9 | 提供 `Task<TimeSpan> PingAsync(CommandFlags flags)` 异步延迟探测 | `IRedisConnectionProvider.PingAsync` |
| FR-01.10 | 提供 `RedisValue StringGet(RedisKey key, CommandFlags flags)` 同步字符串读取 | `IRedisConnectionProvider.StringGet` |
| FR-01.11 | 提供 `void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler)` 订阅 Pub/Sub 频道 | `IRedisConnectionProvider.Subscribe` |
| FR-01.12 | 接口继承 `IDisposable` | `IRedisConnectionProvider : IDisposable` |

### FR-02: RedisConnectionProvider — 薄包装实现

`RedisConnectionProvider` 是 Core 注册的 `IConnectionMultiplexer` 的薄包装，
不再自管连接、不再 Lazy 连接、不再 5 秒节流重连、不再挂事件处理器。
`ConnectionMultiplexer` 自动重连，`AbortOnConnectFail=false` 让命令排队等待（由 Core 负责）。
连接可能为 null（孤岛模式）——所有读写操作降级为空/false/零。

| 编号 | 需求 |
|------|------|
| FR-02.1 | 构造函数注入 `IConnectionMultiplexer?`（由 Agent.Core 的 `AddAgentCore` 注册） |
| FR-02.2 | 连接为 null 时进入孤岛模式，不抛异常 |
| FR-02.3 | `IsAvailable` 返回 `_connection is { IsConnected: true }`（纯探测，不触发重连） |
| FR-02.4 | 孤岛模式下 `StringGetAsync` 返回 `RedisValue.Null` |
| FR-02.5 | 孤岛模式下 `StringSetAsync` 返回 `false`（使用 `When.Always` 标志） |
| FR-02.6 | 孤岛模式下 `KeyDeleteAsync` 返回 `false` |
| FR-02.7 | 孤岛模式下 `SetMembersAsync` 返回空数组 `Array.Empty<RedisValue>()` |
| FR-02.8 | 孤岛模式下 `SetAddAsync` 返回 `false` |
| FR-02.9 | 孤岛模式下 `PingAsync` 返回 `TimeSpan.Zero` |
| FR-02.10 | 孤岛模式下 `StringGet` 返回 `RedisValue.Null` |
| FR-02.11 | `GetDatabase(int)` 在连接不可用时抛出 `InvalidOperationException` |
| FR-02.12 | `GetServer(int)` 在连接不可用时返回 `null` |
| FR-02.13 | `Subscribe` 在连接不可用时为空操作（不抛异常） |
| FR-02.14 | `Dispose` 为空操作：`IConnectionMultiplexer` 生命周期由 DI 容器管理 |

以下能力已在批次二移除，不再存在：

- 从 `IConfiguration` 读取连接字符串、`AbortOnConnectFail=false`、`ConnectTimeout` 配置（由 Core 负责）
- Lazy 初始化、`_nextRetryAt` 5 秒节流重连、`lock(_sync)` 线程安全
- `ConnectionFailed` / `ConnectionRestored` / `ErrorMessage` 事件订阅
- 新连接建立时旧连接事件移除与 Dispose
- `ThrowIfDisposed` / `ObjectDisposedException`

### FR-03: DI 注册策略

| 编号 | 需求 |
|------|------|
| FR-03.1 | 注册 `RedisConnectionProvider` 为 `IRedisConnectionProvider` Singleton |
| FR-03.2 | 测试中通过 DI 注入 `FakeRedisConnectionProvider` 替代 |

### FR-05: 健康检查

| 编号 | 需求 |
|------|------|
| FR-05.1 | `RedisHealthCheck` 在 `IsAvailable == false` 时返回 `Degraded` |
| FR-05.2 | `RedisHealthCheck` 在 `PingAsync` 成功时返回 `Healthy` |
| FR-05.3 | `RedisHealthCheck` 在 `PingAsync` 抛出异常时返回 `Unhealthy`（含异常详情） |

---

## 2. 验收标准（AC）

### AC-01: RedisHealthCheck 健康检查

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-01.1 | Redis 不可用时返回 Degraded 状态 | `RedisHealthCheckTests.Returns_degraded_when_redis_not_available` |
| AC-01.2 | Ping 成功时返回 Healthy 状态 | `RedisHealthCheckTests.Returns_healthy_when_ping_succeeds` |
| AC-01.3 | Ping 抛异常时返回 Unhealthy 状态且包含异常 | `RedisHealthCheckTests.Returns_unhealthy_when_ping_throws` |

### AC-02: RedisConnectionProvider 孤岛模式

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-02.1 | 无连接字符串时进入孤岛模式，不抛异常 | [当前无测试覆盖] |
| AC-02.2 | 孤岛模式下 StringGetAsync 返回 RedisValue.Null | [当前无测试覆盖] |
| AC-02.3 | 孤岛模式下 StringSetAsync 返回 false | [当前无测试覆盖] |
| AC-02.4 | 孤岛模式下 KeyDeleteAsync 返回 false | [当前无测试覆盖] |
| AC-02.5 | 孤岛模式下 SetMembersAsync 返回空数组 | [当前无测试覆盖] |
| AC-02.6 | 孤岛模式下 SetAddAsync 返回 false | [当前无测试覆盖] |
| AC-02.7 | 孤岛模式下 PingAsync 返回 TimeSpan.Zero | [当前无测试覆盖] |
| AC-02.8 | 孤岛模式下 StringGet 返回 RedisValue.Null | [当前无测试覆盖] |
| AC-02.9 | 孤岛模式下 GetDatabase 抛出 InvalidOperationException | [当前无测试覆盖] |
| AC-02.10 | 孤岛模式下 GetServer 返回 null | [当前无测试覆盖] |
| AC-02.11 | 孤岛模式下 Subscribe 为空操作 | [当前无测试覆盖] |

### AC-03: RedisConnectionProvider 连接重试

连接重试 / 节流 / 事件处理已移至 Core 的 `IConnectionMultiplexer` 注册，Engine 不再负责。
`AC-03`、`AC-04`、`AC-05` 已从 Engine 文档移除。

### AC-06: DI 注册

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-06.1 | 注册 `RedisConnectionProvider` 为 `IRedisConnectionProvider` Singleton（注入 Core 的 `IConnectionMultiplexer`） | `ServiceCollectionExtensions` |

### AC-08: 配置消费方 — ConfigProvider

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-08.1 | Redis 不可用时跳过 Redis 查找，进入孤岛模式 | `ConfigProviderTests` [间接覆盖] |
| AC-08.2 | Redis 可用时从 `agent:config:{agentId}` 加载配置 | `ConfigProviderTests.GetConfigAsync_loads_from_redis_when_snapshot_miss` |
| AC-08.3 | 从 `agent:published:index` 获取 Agent 列表 | [当前无测试覆盖] |

### AC-09: 配置热加载 — HotReloadService

| 编号 | 验收标准 | 测试覆盖 |
|------|----------|----------|
| AC-09.1 | 订阅 `agent:config:updates` 频道 | `HotReloadTests` [间接覆盖] |
| AC-09.2 | 订阅所有 Legacy 频道 | `HotReloadTests.ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot` |
| AC-09.3 | 结构化消息触发全量刷新 | `HotReloadTests.ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis` |
| AC-09.4 | FullSync 清空快照 | `HotReloadTests.ProcessMessage_FullSync_ClearsSnapshot` |
| AC-09.5 | ConfigUpdate 类型触发从 Redis 全量刷新 | `HotReloadTests.ProcessMessage_ConfigUpdate_RefreshesFullConfigFromRedis` |
| AC-09.6 | 空白消息被忽略 | `HotReloadTests.ProcessMessage_IgnoresBlankPayload` |
| AC-09.7 | 无效 JSON 不覆盖已有 Snapshot | `HotReloadTests.ProcessMessage_InvalidJson_DoesNotOverwriteExistingSnapshot` |
| AC-09.8 | Legacy 注册频道不修改 Snapshot | `HotReloadTests.ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot` |

---

## 3. 非功能需求（NFR）

| 编号 | 需求 |
|------|------|
| NFR-01 | 薄包装不自管连接，线程安全由底层 `IConnectionMultiplexer` 保证 |
| NFR-02 | 连接失败不阻塞调用线程（连接为 null 时返回安全默认值） |
| NFR-03 | 重连 / 节流由 `ConnectionMultiplexer` 自动处理，Engine 不再负责 |
| NFR-04 | 连接配置（AbortOnConnectFail / ConnectTimeout）由 Agent.Core 负责 |
