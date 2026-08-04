# ServiceRegistration - 任务清单

```json
[
  {
    "id": "SR-001",
    "title": "定义 IEngineRegistry 接口",
    "description": "定义 RegisterAsync、HeartbeatAsync、DeregisterAsync、IsRegistered 接口成员",
    "status": "implemented",
    "file": "src/Engine/Abstractions/IEngineRegistry.cs"
  },
  {
    "id": "SR-002",
    "title": "实现 RedisRegistry 注册逻辑",
    "description": "生成 EngineId，序列化 RegistryEntry，StringSetAsync 写入 Redis 并设置 TTL",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-003",
    "title": "实现 RedisRegistry 心跳逻辑",
    "description": "更新 LastHeartbeat 和 Load，刷新 TTL；失败时标记 IsRegistered=false",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-004",
    "title": "实现 RedisRegistry 注销逻辑",
    "description": "KeyDeleteAsync 删除注册 key；失败时记录 Warning 日志",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-005",
    "title": "实现负载计算算法",
    "description": "内存压力 40% + GC 压力 30% + 线程池压力 30%，结果 clamp 到 [0,100]",
    "status": "implemented",
    "file": "src/Engine/Registry/RedisRegistry.cs"
  },
  {
    "id": "SR-006",
    "title": "实现 HeartbeatService 后台服务",
    "description": "BackgroundService：等待端口检测 → 注册 → 心跳循环，含重试逻辑",
    "status": "implemented",
    "file": "src/Engine/Redis/HeartbeatService.cs"
  },
  {
    "id": "SR-007",
    "title": "实现端口自动检测",
    "description": "优先读取 ASPNETCORE_HTTP_PORTS，其次 ASPNETCORE_URLS，默认 80",
    "status": "implemented",
    "file": "src/Engine/Redis/HeartbeatService.cs"
  },
  {
    "id": "SR-008",
    "title": "定义 HeartbeatOptions 配置模型",
    "description": "IntervalSeconds=10, RetryDelaySeconds=5, RegistryTtlSeconds=30, AdvertisedHost, AdvertisedPort",
    "status": "implemented",
    "file": "src/Engine/Models/HeartbeatOptions.cs"
  },
  {
    "id": "SR-009",
    "title": "定义 RegistryEntry 数据模型",
    "description": "EngineId, Host, Port, Load, LastHeartbeat",
    "status": "implemented",
    "file": "src/Engine/Models/RegistryEntry.cs"
  },
  {
    "id": "SR-010",
    "title": "DI 注册与停机编排",
    "description": "ServiceCollectionExtensions 注册 IEngineRegistry/HeartbeatService；Program.cs 停机时先 ShutdownService 再 DeregisterAsync",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs, src/Host/Program.cs"
  },
  {
    "id": "SR-011",
    "title": "编写 ServiceRegistration 单元测试",
    "description": "覆盖注册成功/失败、心跳更新、注销、负载计算、端口检测等场景",
    "status": "pending",
    "file": ""
  }
]
```
