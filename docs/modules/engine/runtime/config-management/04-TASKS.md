# ConfigManagement - 任务清单

```json
[
  {
    "id": "CM-001",
    "title": "定义 IConfigSnapshot 接口",
    "description": "定义 GetConfig/SetConfig/TryGetConfig（按 key 和 agentId+configType）及 GetVersion/SetVersion 方法",
    "status": "implemented",
    "file": "src/Engine/Abstractions/IConfigSnapshot.cs"
  },
  {
    "id": "CM-002",
    "title": "实现 ConfigSnapshot 内存缓存",
    "description": "基于 IMemoryCache 实现，lock 保护写入，支持 BuildCacheKey/BuildVersionKey 格式化",
    "status": "implemented",
    "file": "src/Engine/Models/ConfigSnapshot.cs"
  },
  {
    "id": "CM-003",
    "title": "实现 SetFullConfig 批量写入",
    "description": "一次性写入 FullAgentConfig/LLMSettings/RAGSettings/MCPSettings/SkillsSettings 及版本号",
    "status": "implemented",
    "file": "src/Engine/Models/ConfigSnapshot.cs"
  },
  {
    "id": "CM-004",
    "title": "实现 ConfigProvider 三级读取链",
    "description": "Snapshot → Redis → Mock 降级，含 EnrichWithSecureSecrets 和 AllowMockAgent 解析",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-005",
    "title": "实现无 agentId 调用抛异常",
    "description": "GetConfigAsync() 无 agentId 重载始终抛出 InvalidOperationException",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-006",
    "title": "实现敏感信息注入",
    "description": "EnrichWithSecureSecrets 从 LLM__APIKEY/LLM_API_KEY 环境变量注入 API Key",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-007",
    "title": "实现 AllowMockAgent 解析",
    "description": "优先级：配置值 > 环境变量 > IsDevelopment/IsTesting",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-008",
    "title": "实现 ListAgentsAsync",
    "description": "从 agent:published:index 读取已发布 Agent 列表并返回 AgentSummary",
    "status": "implemented",
    "file": "src/Engine/Config/ConfigProvider.cs"
  },
  {
    "id": "CM-009",
    "title": "DI 注册",
    "description": "ConfigSnapshot 注册为 Singleton，ConfigProvider 注册为 IAgentConfigProvider",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "CM-010",
    "title": "编写 ConfigProviderTests",
    "description": "测试无 agentId 异常、Snapshot 命中、Redis 读取、Mock 降级等场景",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/Config/ConfigProviderTests.cs"
  },
  {
    "id": "CM-011",
    "title": "编写 ConfigSnapshotTests",
    "description": "测试 SetConfig/GetConfig 往返、TryGetConfig 缺失、SetFullConfig 全量写入、版本管理",
    "status": "implemented",
    "file": "test/OpenAgent.Engine.Tests/Config/ConfigSnapshotTests.cs"
  }
]
```
