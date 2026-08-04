# ConfigHotReload - 编码约定

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
