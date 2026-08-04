# ConfigHotReload - 任务清单

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
