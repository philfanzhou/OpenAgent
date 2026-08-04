# 04-TASKS — Redis 连接管理任务清单

> 关联文档：[02-SPEC.md](./02-SPEC.md) | [03-DESIGN.md](./03-DESIGN.md) | [05-TESTS.md](./05-TESTS.md)

---

```json
[
  {
    "id": "T-01",
    "title": "定义 IRedisConnectionProvider 接口",
    "description": "定义 internal 接口，包含 IsAvailable、GetServer、GetDatabase、StringGetAsync、StringSetAsync、KeyDeleteAsync、SetMembersAsync、SetAddAsync、PingAsync、StringGet、Subscribe 方法，继承 IDisposable",
    "file": "src/Engine/Abstractions/IRedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-01"]
  },
  {
    "id": "T-02",
    "title": "实现 RedisConnectionProvider — 薄包装",
    "description": "注入 IConnectionMultiplexer?（Core 注册），连接为 null 时进入孤岛模式；IsAvailable 纯探测不触发重连",
    "file": "src/Engine/Redis/RedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-02.1", "FR-02.2", "FR-02.3"]
  },
  {
    "id": "T-05",
    "title": "实现 RedisConnectionProvider — 孤岛模式操作",
    "description": "所有数据操作在连接不可用时返回安全默认值：StringGetAsync→Null, StringSetAsync→false（When.Always）, KeyDeleteAsync→false, SetMembersAsync→空数组, SetAddAsync→false, PingAsync→Zero, StringGet→Null, GetDatabase→InvalidOperationException, GetServer→null, Subscribe→空操作",
    "file": "src/Engine/Redis/RedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-02.4", "FR-02.5", "FR-02.6", "FR-02.7", "FR-02.8", "FR-02.9", "FR-02.10", "FR-02.11", "FR-02.12", "FR-02.13"]
  },
  {
    "id": "T-06",
    "title": "RedisConnectionProvider Dispose 为空操作",
    "description": "IConnectionMultiplexer 生命周期由 DI 容器管理，Dispose 不再释放资源",
    "file": "src/Engine/Redis/RedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": ["FR-02.14"]
  },
  {
    "id": "T-16",
    "title": "实现 DI 注册策略",
    "description": "在 ServiceCollectionExtensions.AddAgentEngine 中注册 RedisConnectionProvider 为 IRedisConnectionProvider Singleton。测试中通过 DI 注入 FakeRedisConnectionProvider 替代。",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs",
    "status": "implemented",
    "specRef": ["FR-04.1", "FR-04.2", "FR-04.3"]
  },
  {
    "id": "T-17",
    "title": "实现 RedisHealthCheck",
    "description": "实现 IHealthCheck：IsAvailable==false→Degraded, PingAsync 成功→Healthy, PingAsync 异常→Unhealthy",
    "file": "src/Engine/Redis/RedisHealthCheck.cs",
    "status": "implemented",
    "specRef": ["FR-05.1", "FR-05.2", "FR-05.3"]
  },
  {
    "id": "T-18",
    "title": "实现 RedisRegistry — Engine 注册与心跳",
    "description": "实现 IEngineRegistry：RegisterAsync/HeartbeatAsync 使用 StringSetAsync 写入 engine:registry:{id}，DeregisterAsync 使用 KeyDeleteAsync",
    "file": "src/Engine/Registry/RedisRegistry.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-19",
    "title": "实现 HeartbeatService — 后台心跳服务",
    "description": "BackgroundService：周期性调用 RegisterAsync/HeartbeatAsync，支持端口检测和重试",
    "file": "src/Engine/Redis/HeartbeatService.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-20",
    "title": "实现 ConfigProvider — Redis 配置加载",
    "description": "从 Redis 读取 agent:config:{id} 和 agent:published:index，支持 Snapshot 缓存和孤岛模式降级",
    "file": "src/Engine/Config/ConfigProvider.cs",
    "status": "implemented",
    "specRef": ["AC-08"]
  },
  {
    "id": "T-21",
    "title": "实现 HotReloadService — Pub/Sub 配置热加载",
    "description": "订阅 agent:config:updates 和 Legacy 频道，处理结构化/非结构化消息，更新 ConfigSnapshot",
    "file": "src/Engine/Reload/HotReloadService.cs",
    "status": "implemented",
    "specRef": ["AC-09"]
  },
  {
    "id": "T-22",
    "title": "实现 RedisSkillRegistrar — 技能注册",
    "description": "IHostedService：启动时从 skill:published:index 和 skill:registry:{name} 加载技能并注册到 IToolRegistry",
    "file": "src/Engine/Redis/RedisSkillRegistrar.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-23",
    "title": "实现 RedisLlmRegistrar — LLM 配置注册",
    "description": "IHostedService：启动时从 llm:published:index 和 llm:registry:{id} 加载 LLM 配置并注册到 ILlmRegistry",
    "file": "src/Engine/Redis/RedisLlmRegistrar.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-24",
    "title": "实现 RedisRagRegistrar — RAG 实例注册",
    "description": "IHostedService：启动时从 rag:published:index 和 rag:registry:{id} 加载 RAG 配置并注册到 IRagRegistry",
    "file": "src/Engine/Redis/RedisRagRegistrar.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-25",
    "title": "实现 FakeRedisConnectionProvider — 测试替身",
    "description": "内存 Dictionary 存储的 IRedisConnectionProvider 实现，用于单元测试",
    "file": "test/OpenAgent.Engine.Tests/TestDoubles/FakeRedisConnectionProvider.cs",
    "status": "implemented",
    "specRef": []
  },
  {
    "id": "T-26",
    "title": "编写 RedisHealthCheck 单元测试",
    "description": "测试 Degraded/Healthy/Unhealthy 三种状态",
    "file": "test/OpenAgent.Engine.Tests/HealthChecks/RedisHealthCheckTests.cs",
    "status": "implemented",
    "specRef": ["AC-01"]
  },
  {
    "id": "T-27",
    "title": "编写 HotReloadService 单元测试",
    "description": "测试结构化消息、Legacy 消息、过期版本、空白消息、无效 JSON 等场景",
    "file": "test/OpenAgent.Engine.Tests/Config/HotReloadTests.cs",
    "status": "implemented",
    "specRef": ["AC-09"]
  },
  {
    "id": "T-28",
    "title": "编写 ConfigProvider 单元测试",
    "description": "测试 Snapshot 加载、Redis 加载、Mock 降级等场景",
    "file": "test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs",
    "status": "implemented",
    "specRef": ["AC-08"]
  }
]
```
