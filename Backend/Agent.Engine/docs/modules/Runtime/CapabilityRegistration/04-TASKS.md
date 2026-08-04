# CapabilityRegistration - 任务清单

```json
[
  {
    "id": "CR-001",
    "title": "实现 RedisLlmRegistrar",
    "description": "IHostedService：读取 llm:published:index，加载 LlmProviderProfile，调用 ILlmRegistry.Register",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisLlmRegistrar.cs"
  },
  {
    "id": "CR-002",
    "title": "实现 RedisRagRegistrar",
    "description": "IHostedService：读取 rag:published:index，加载 RagInstanceConfig，调用 IRagRegistry.Register",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisRagRegistrar.cs"
  },
  {
    "id": "CR-003",
    "title": "实现 RedisSkillRegistrar",
    "description": "IHostedService：读取 skill:published:index，加载 SkillInstanceConfig，创建 RedisMockSkill，调用 IToolRegistry.RegisterTool",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisSkillRegistrar.cs"
  },
  {
    "id": "CR-004",
    "title": "实现 RedisMockSkill HttpEndpoint 执行",
    "description": "内部类：HttpEndpoint 类型 POST JSON 到 EndpointUrl（30s 超时），其他类型返回错误字符串",
    "status": "implemented",
    "file": "src/Engine/Redis/RedisSkillRegistrar.cs"
  },
  {
    "id": "CR-005",
    "title": "DI 注册三个 Registrar",
    "description": "注册 RedisSkillRegistrar、RedisRagRegistrar、RedisLlmRegistrar 为 HostedService",
    "status": "implemented",
    "file": "src/Engine/Extensions/ServiceCollectionExtensions.cs"
  },
  {
    "id": "CR-006",
    "title": "编写 CapabilityRegistration 单元测试",
    "description": "覆盖 LLM/RAG/Skill 注册成功/失败、Redis 不可用跳过、RedisMockSkill 执行等场景",
    "status": "pending",
    "file": ""
  }
]
```
