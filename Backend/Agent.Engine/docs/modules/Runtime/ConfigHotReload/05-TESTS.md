# ConfigHotReload - 测试文档

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
