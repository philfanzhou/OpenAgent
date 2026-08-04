# ConfigHotReload - 设计文档

## 架构概览

`HotReloadService` 是 Redis Pub/Sub 的宿主适配器；它不再解释消息内容或直接写入
`ConfigSnapshot`。消息协议、全量刷新、遗留兼容、版本比较与增量配置写入由独立协作类承担。

```
Config Management ── Publish ──> Redis Pub/Sub
                                      │
                                      ▼
                              HotReloadService
                           (订阅、取消、异常边界)
                                      │ ProcessMessage
                                      ▼
                           ConfigUpdateDispatcher
                     ┌────────────────┼────────────────┐
                     ▼                ▼
          LegacyMessageHandler  FullConfigRefresher
                     │                │
                     └─────────────── ConfigSnapshot (IMemoryCache + TTL) ─┘
                                      │
                                      ▼
                              各消费方读取快照
```

## 职责与文件清单

| 文件路径 | 职责 |
|---|---|
| `src/Engine/Reload/HotReloadService.cs` | 订阅当前与遗留频道，桥接 Redis 回调，提供可测试的 `ProcessMessage` facade。 |
| `src/Engine/Reload/ConfigUpdateDispatcher.cs` | 解析结构化消息、控制全量/FullSync 分支，并在顶层隔离异常。 |
| `src/Engine/Reload/LegacyMessageHandler.cs` | 兼容纯文本遗留通知；仅 agent 配置频道触发全量刷新。 |
| `src/Engine/Reload/FullConfigRefresher.cs` | 从 `agent:config:{agentId}` 读取并反序列化完整配置，写入快照；未命中时逐出该 agent。 |
| `src/Engine/Models/ConfigSnapshot.cs` | 基于 `IMemoryCache` 的配置快照，带 TTL 绝对过期；是唯一的内存写入目标。 |
| `src/Engine/Config/ConfigSnapshotOptions.cs` | `AbsoluteExpirationMinutes` 选项（默认 5 分钟）。 |
| `src/Engine/Extensions/ServiceCollectionExtensions.cs` | Engine composition root：注册全部协作类和 HostedService。 |

## Redis 依赖与消息协议

| 频道名 | 消息类型 | 行为 |
|---|---|---|
| `agent:config:updates` | 结构化 JSON 或遗留纯文本 | JSON 交给 dispatcher；纯文本按 agentId 触发全量刷新。 |
| `agent:config:changed` | 遗留纯文本 | 按 agentId 触发全量刷新。 |
| `skill:registry:changed`、`llm:registry:changed`、`rag:registry:changed`、`engine:config:changed` | 遗留通知 | 记录通知，不改写 agent 配置快照。 |

结构化 `ConfigUpdate` 包含 `AgentId`、`Type`、`ConfigType`、`Data`、`Version` 和
`Timestamp`。`Type == ConfigUpdate` 和 `IncrementalUpdate` 都从 Redis 全量刷新；
`Type == FullSync` 清空整个快照（无需 AgentId，支持广播）。

## 结构化消息处理流程

```
ConfigUpdateDispatcher.Process(channel, message)
  │
  ├─ 空白 → 忽略并记录结构化日志
  ├─ 非 JSON → LegacyMessageHandler.Process
  ├─ JSON 无法反序列化 → 忽略
  ├─ Type == FullSync → _snapshot.Clear()（无需 AgentId）
  ├─ AgentId 缺失 → 忽略
  └─ FullConfigRefresher.Refresh(agentId)
       ├─ Redis 键存在 → SetFullConfig（同时写入 FullAgentConfig + 四个子配置）
       └─ Redis 键缺失 → Evict(agentId)，返回 false
```

全量刷新管线统一：不再按 `ConfigType` 选择 handler，不再维护版本，不再 patch。
无 `Data` 或反序列化失败时不修改快照。

## TTL 与自愈

`ConfigSnapshot` 包装 `IMemoryCache`，每个条目按 `AbsoluteExpirationMinutes` 绝对过期。
丢失 pub/sub 消息后，过期条目自动清除；下次访问再从 Redis 加载最新值。
`AddAgentEngine` 通过 `services.Configure<ConfigSnapshotOptions>` 从 `ConfigSnapshot` 节读取 TTL。

## DI 与生命周期

`AddAgentEngine` 是唯一 composition root。`ConfigSnapshot`、`FullConfigRefresher`、
`LegacyMessageHandler`、`ConfigUpdateDispatcher` 以 plain singleton 注册；
`HotReloadService` 作为 HostedService 仅依赖已注册 dispatcher。`IMemoryCache` 由
`AddMemoryCache` 提供（Engine-wide）。

由于这些实现均为 `internal`，而 default container 无法选择 internal 构造函数，
registration 仍使用显式开放工厂/泛型重载注册，但不再需要 hand-written lambda；
这样既能在启动时校验依赖图，也不会把重构协作类变成公共 API。所有 singleton 必须线程安全，
且不得依赖 scoped 服务。

## 关键设计决策

1. **订阅与协议解耦**：Redis 生命周期故障不会迫使消息解析逻辑进入 BackgroundService，单元测试可直接调用 dispatcher。
2. **全量统一保证一致性**：`ConfigUpdate` 与 `IncrementalUpdate` 统一走全量刷新；Redis 中完整实体是唯一事实来源，消除增量 patch 的不一致风险。
3. **FullSync 清空快照**：广播信号直接 `_snapshot.Clear()`，让各消费方在下次访问时从 Redis 重新加载。
4. **TTL 自愈替代永久缓存**：绝对过期保证丢失消息后不会无限期服务过期配置。
5. **兼容不扩大行为**：遗留 registry 通知继续被订阅和记录，但不会被误当成 agent 配置刷新。
6. **可观测性不侵入流程**：使用 `EngineLog` 的 `[LoggerMessage]` 事件；日志文本与事件定义集中，业务类只选择事件。
