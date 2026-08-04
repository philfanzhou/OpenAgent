
## Feature


## 核心用户故事

作为 Engine 运维人员，我希望配置变更能通过 Redis Pub/Sub 实时传播到 Engine 节点，以便 Agent 配置更新无需重启服务即可生效。

## 功能简介

ConfigHotReload 通过 Redis Pub/Sub 订阅配置变更通知，实时更新内存中的 ConfigSnapshot。支持结构化 JSON 消息和传统纯文本消息两种格式；所有结构化消息统一从 Redis 全量刷新，FullSync 消息则清空快照。快照条目带 TTL，丢失 pub/sub 消息后不会永久缓存过期配置。

## 关键能力

- **Redis Pub/Sub 订阅**：监听 6 个频道（1 个当前 + 5 个遗留）
- **结构化消息处理**：ConfigUpdate 与 IncrementalUpdate 均从 Redis 全量刷新；FullSync 清空快照
- **TTL 自愈**：快照条目按 `ConfigSnapshotOptions.AbsoluteExpirationMinutes` 绝对过期，丢失消息后自动恢复
- **遗留消息兼容**：非 JSON 消息在 `agent:config:changed` 频道视为 agentId 触发全量刷新
- **即时逐出**：全量刷新时若 Redis 配置键已删除，立即从快照中移除该 agent
 - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
- [../ConfigManagement/01-FEATURE.md](../ConfigManagement/01-FEATURE.md) - 配置管理功能

## Specification


## 功能需求 (FR)

### FR-HR-001: Redis Pub/Sub 订阅

- **当前频道**: `agent:config:updates`（常量 `CurrentUpdatesChannel`）
- **遗留频道**: `agent:config:changed`、`skill:registry:changed`、`llm:registry:changed`、`rag:registry:changed`、`engine:config:changed`
- **行为**: `ExecuteAsync` 中订阅所有频道，然后 `Task.Delay(Timeout.Infinite)` 等待取消

### FR-HR-002: 结构化消息处理

- **消息格式**: JSON，包含 AgentId、Type、ConfigType、Version、Timestamp、Data (JsonElement?)
- **ConfigUpdate / IncrementalUpdate 类型**: 统一从 Redis 全量刷新 `agent:config:{agentId}`，由 `FullConfigRefresher` 调用 `SetFullConfig`
- **FullSync 类型**: 清空整个快照（`_snapshot.Clear()`），不再保留旧配置
- **刷新未命中**: 全量刷新时若 Redis 配置键已删除，`FullConfigRefresher` 调用 `ConfigSnapshot.Evict(agentId)` 立即移除该 agent 的全部子配置

### FR-HR-003: TTL 自愈

- **行为**: `ConfigSnapshot` 使用 `IMemoryCache` + `ConfigSnapshotOptions.AbsoluteExpirationMinutes`（默认 5 分钟）绝对过期
- **目的**: 丢失 pub/sub 消息后，过期配置不会永久缓存；下次命中再从 Redis 加载最新值
- **校验**: `AbsoluteExpirationMinutes <= 0` 时构造函数抛出 `ArgumentOutOfRangeException`，阻止立即过期的无效配置

### FR-HR-004: 已删除的能力

以下能力已在批次一移除，不再存在：

- `IConfigUpdateHandler` / `ConfigUpdateHandlerBase` / 六种 per-ConfigType handler（`FullAgentConfigHandler`、`LlmSettingsHandler`、`RagSettingsHandler`、`McpSettingsHandler`、`SkillsSettingsHandler`、`AgentSettingsHandler`）
- `ConfigVersionComparer` 版本比较与 `ConfigSnapshot.GetVersion` / 版本跟踪
- `PatchFullConfig` 增量 patch 语义

### FR-HR-006: 遗留消息处理

- **非 JSON 消息**: 交给 `LegacyMessageHandler`
- **`agent:config:changed` 频道**: 将 payload 视为 agentId，从 Redis 全量刷新
- **`agent:config:updates` 频道非 JSON**: 同样从 Redis 全量刷新
- **其他遗留频道**: 仅记录日志，不修改 Snapshot

### FR-HR-007: JSON 检测

- **方法**: `ConfigUpdateDispatcher.LooksLikeJson(string payload)` — 消息以 `{` 或 `[` 开头视为 JSON
- **非 JSON**: 走遗留消息处理流程

## 验收标准 (AC)

### AC-HR-001: 结构化 ConfigUpdate 全量刷新

- **Given** Redis 中存在 `agent:config:agent-a` 的配置
- **When** 收到 `agent:config:updates` 频道的 `{"agentId":"agent-a","type":"ConfigUpdate"}` 消息
- **Then** 从 Redis 读取全量配置，更新 Snapshot

### AC-HR-002: 结构化 IncrementalUpdate 同样触发全量刷新

- **Given** Snapshot 中存在 agent-b 的配置
- **When** 收到 `{"agentId":"agent-b","type":"IncrementalUpdate","configType":"LLMSettings","version":9,"data":{...}}` 消息
- **Then** 忽略 payload 中的子配置 data，从 Redis 全量刷新 agent-b 的完整配置

### AC-HR-003: 全量刷新在 Redis 键删除时逐出快照

- **Given** Snapshot 中存在 agent-x 的配置，但 Redis 中 `agent:config:agent-x` 已删除
- **When** 收到任意结构化更新消息指向 agent-x
- **Then** `FullConfigRefresher` 未命中，调用 `ConfigSnapshot.Evict("agent-x")` 移除全部子配置

### AC-HR-004: 遗留消息触发全量刷新

- **Given** Redis 中存在 `agent:config:agent-a` 的配置
- **When** 收到 `agent:config:changed` 频道的纯文本消息 `"agent-a"`
- **Then** 从 Redis 全量刷新 agent-a 的配置

### AC-HR-005: 遗留注册频道不修改 Snapshot

- **Given** Snapshot 中存在 agent-f 的配置
- **When** 收到 `skill:registry:changed` 频道的消息
- **Then** 仅记录日志，不修改 Snapshot

### AC-HR-006: 空消息忽略

- **Given** 任意频道
- **When** 收到空白消息
- **Then** 忽略，不修改 Snapshot

### AC-HR-007: 无效 JSON 不覆盖现有配置

- **Given** Snapshot 中存在 agent-e 的 LLMSettings
- **When** 收到无效 JSON 消息 `"{ invalid json"`
- **Then** 保留原有配置不变

### AC-HR-008: FullSync 清空快照

- **Given** 任意状态
- **When** 收到 `{"type":"FullSync"}` 消息（无需 AgentId）
- **Then** 调用 `_snapshot.Clear()` 清空整个快照

### AC-HR-009: TTL 绝对过期自动清除

- **Given** Snapshot 中 agent-ttl 的 LLMSettings 已缓存
- **When** 超过 `AbsoluteExpirationMinutes` 未收到任何更新
- **Then** 缓存条目自动过期，下次访问从 Redis 重新加载

### AC-HR-010: 非法 TTL 在构造时拒绝

- **Given** `AbsoluteExpirationMinutes = 0` 或负数
- **When** 构造 `ConfigSnapshot`
- **Then** 抛出 `ArgumentOutOfRangeException`，参数名 `ConfigSnapshot:AbsoluteExpirationMinutes`

## ConfigUpdate 消息结构

```json
{
  "agentId": "string",
  "type": "ConfigUpdate | IncrementalUpdate | FullSync",
  "configType": "FullAgentConfig | AgentSettings | LLM | LLMSettings | RAG | RAGSettings | MCP | MCPSettings | Skills | SkillsSettings",
  "version": 0,
  "timestamp": "2026-01-01T00:00:00Z",
  "data": {}
}
```

## Design


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

## Tasks


| ID | 任务 | 状态 | 实现位置 |
|---|---|---|---|
| HR-001 | 定义当前更新频道和五个遗留频道 | 已完成 | `Reload/HotReloadService.cs` |
| HR-002 | 用 HostedService 订阅频道，并在取消时退出 | 已完成 | `Reload/HotReloadService.cs` |
| HR-003 | 从订阅宿主提取结构化消息编排（全量/FullSync 分支） | 已完成 | `Reload/ConfigUpdateDispatcher.cs` |
| HR-004 | 提取遗留纯文本通知处理 | 已完成 | `Reload/LegacyMessageHandler.cs` |
| HR-005 | 提取 Redis 全量配置读取与反序列化，未命中时逐出 | 已完成 | `Reload/FullConfigRefresher.cs` |
| HR-006 | 以 `IMemoryCache` + 绝对过期 TTL 实现快照自愈 | 已完成 | `Models/ConfigSnapshot.cs`、`Config/ConfigSnapshotOptions.cs` |
| HR-007 | FullSync 清空快照、非法 TTL 构造拒绝 | 已完成 | `Reload/ConfigUpdateDispatcher.cs`、`Models/ConfigSnapshot.cs` |
| HR-008 | 在 composition root 注册 dispatcher、协作类及 HostedService | 已完成 | `Extensions/ServiceCollectionExtensions.cs` |
| HR-009 | 覆盖消息主流程、TTL 过期、FullSync 清空与 DI 构建回归测试 | 已完成 | `test/.../Config/HotReloadTests.cs`、`ConfigUpdateRegistrationTests.cs` |

## 发布环境待验收

以下不是代码拆分任务，需在具备 Redis 与配置管理端的环境执行：

- 验证六个频道的真实发布、断线重连和服务重启后的订阅恢复。
- 用真实 `agent:config:{agentId}` 数据验证全量刷新与增量 patch 一致性。
- 与真实 LLM、SSO 及部署健康检查一起执行 ManualE2E；默认单元测试不替代该门禁。

## Tests


## 自动化覆盖

### `HotReloadTests`

文件：`test/OpenAgent.Engine.Tests/Config/HotReloadTests.cs`

| 场景 | 代表测试 |
|---|---|
| 遗留 agent 配置通知触发全量刷新 | `ProcessMessage_RefreshesSnapshotFromLegacyAgentChannel` |
| `ConfigUpdate` 从 Redis 刷新完整配置 | `ProcessMessage_ConfigUpdate_RefreshesFullConfigFromRedis` |
| 结构化 IncrementalUpdate 同样触发全量刷新（忽略 payload 子配置） | `ProcessMessage_TypedUpdate_RefreshesFullConfigFromRedis` |
| 低版本结构化更新仍触发全量刷新（无版本保护） | `ProcessMessage_TypedUpdateWithLowerVersion_StillRefreshesFromRedis` |
| 未知 ConfigType 仍触发全量刷新 | `ProcessMessage_UnknownConfigType_RefreshesFullConfigFromRedis` |
| FullSync 清空快照 | `ProcessMessage_FullSync_ClearsSnapshot` |
| FullSync 无需 AgentId（广播） | `ProcessMessage_FullSyncWithoutAgentId_ClearsSnapshot` |
| Redis 键已删除时全量刷新逐出快照 | `ProcessMessage_TypedUpdateWithoutRedisConfig_EvictsSnapshot` |
| 空白消息、无效 JSON 不覆盖快照 | `ProcessMessage_IgnoresBlankPayload`、`ProcessMessage_InvalidJson_DoesNotOverwriteExistingSnapshot` |
| 遗留 registry 通知不改快照 | `ProcessMessage_LegacyRegistryChannel_DoesNotMutateSnapshot` |
| 枚举字符串兼容 | `ProcessMessage_LegacyRefresh_AcceptsStringEnumValues` |
| TTL 绝对过期自动清除 | `SetConfig_AfterAbsoluteExpiration_ExpiresEntry` |
| 非法 TTL 构造拒绝 | `Constructor_ZeroTtl_ThrowsArgumentOutOfRangeException`、`Constructor_NegativeTtl_ThrowsArgumentOutOfRangeException` |

测试通过 `HotReloadService.ProcessMessage` 覆盖 facade 行为；其协作对象使用与生产相同的 dispatcher、
全量刷新器、遗留处理器组合（无 handler / 版本比较器），避免把协议逻辑重新复制到测试专用路径。

### `ConfigUpdateRegistrationTests`

文件：`test/OpenAgent.Engine.Tests/Config/ConfigUpdateRegistrationTests.cs`

| 场景 | 代表测试 |
|---|---|
| Engine composition root 可解析 dispatcher，并只创建一个 singleton | `AddAgentEngine_RegistersReloadPipelineAsSingletons` |
| 已注册 dispatcher 能执行结构化更新（全量刷新） | `RegisteredDispatcher_TypedUpdateRefreshesSnapshotFromRedis` |

此组测试防止重构后出现“构造函数可注入但 composition root 未注册”的启动时缺陷。

## 回归约束

- 未知类型、无 `Data`、无 `AgentId`、反序列化失败不得改变已有快照。
- 所有结构化消息（ConfigUpdate / IncrementalUpdate）走全量 Redis 刷新。
- FullSync 必须清空快照，且支持无 AgentId 广播。
- 全量刷新 Redis 键缺失时必须逐出该 agent 快照。
- TTL 绝对过期后条目自动清除。
- Redis 回调异常必须被 HostedService 的回调边界记录，不能中断后续消息处理。

## 发布环境验证

自动化单元测试不模拟 Redis Pub/Sub 的真实断线、配置管理端发布或外部 LLM/SSO。发布前按
`TestCode/docs/e2e-test-guide.md` 和 `RealServiceTests` 的 `ManualE2E` 分类，在设置
`OPENAGENT_RUN_MANUAL_E2E=true` 的环境验证：频道发布、重启后订阅、全量/增量一致性及 Engine 健康状态。

## Conventions


## 职责边界

| 类型 | 应承担的职责 | 不应承担的职责 |
|---|---|---|
| `HotReloadService` | Redis 订阅、取消、回调异常边界 | JSON 解析、直接更新配置对象 |
| `ConfigUpdateDispatcher` | 消息协议分支（全量/FullSync）、顶层错误隔离 | Redis key 读取、具体配置字段转换 |
| `FullConfigRefresher` | 从 Redis 读取并写入完整快照，未命中时逐出 | Pub/Sub 订阅、增量配置分发 |
| `LegacyMessageHandler` | 旧频道与纯文本 payload 的兼容语义 | 结构化 JSON 解析 |

全量刷新管线统一：不再新增 per-ConfigType handler，不再 patch，不再维护版本。
新增配置类型只需调整 `FullConfigRefresher` 的反序列化模型，不得把 `switch` 或
具体转换逻辑重新塞回 `HotReloadService`。

## DI 与可见性

- 热加载协作对象与 `ConfigSnapshot` 是 singleton；不得依赖 scoped 服务或保存请求状态。
- `HotReloadService` 只接受 `IRedisConnectionProvider`、`ConfigUpdateDispatcher` 和 typed logger。
- 协作类保持 `internal`。由于默认容器无法选择 internal 构造函数，
  `ServiceCollectionExtensions` 使用显式泛型/factory 注册；不要为了 DI 将其改为 public。

## 消息规则

- 所有结构化消息（ConfigUpdate / IncrementalUpdate）统一从 Redis 全量刷新，忽略 payload 中的子配置 data。
- `FullSync` 清空整个快照，无需 AgentId（支持广播）。
- 全量刷新 Redis 键缺失时，`FullConfigRefresher` 调用 `Evict(agentId)` 立即移除该 agent。
- DTO 留在 `Reload/Dtos`；不要以 Engine 内部消息格式扩展 Contracts 公共模型。

## TTL 规则

- `ConfigSnapshot` 每个条目按 `AbsoluteExpirationMinutes` 绝对过期（默认 5 分钟）。
- `AbsoluteExpirationMinutes <= 0` 在构造时抛出 `ArgumentOutOfRangeException`，不得配置立即过期的无效值。

## 日志与错误处理

- 使用 `EngineLog` 的 `[LoggerMessage]` 方法；不要在生产热加载代码中直接调用 `ILogger.Log*`。
- 空 payload、缺 AgentId、未知类型、旧版本、Redis 无配置属于可恢复事件，记录后返回。
- 单条消息处理异常由 `ConfigUpdateDispatcher.Process` 捕获并记录；Redis callback 仍保留防御性 catch，
  以免异常逃出订阅回调。
- 日志字段使用 `{Channel}`、`{AgentId}`、`{ConfigType}`、`{Version}`、`{CurrentVersion}` 和异常；
  payload 仅按现有事件策略记录，避免在新增日志中泄漏配置密钥。
