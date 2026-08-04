# 能力注册（Engine Runtime）

Engine 启动时从 Redis 加载 LLM/RAG/Skill 能力配置，注册到内存 Registry 供 Agent 执行时使用。

## 核心能力

- **LLM 注册**：从 `llm:published:index` 加载 LlmProviderProfile
- **RAG 注册**：从 `rag:published:index` 加载 RagInstanceConfig
- **Skill 注册**：从 `skill:published:index` 加载 SkillInstanceConfig
- **HttpEndpoint Skill 代理**：RedisMockSkill 对远程 Skill 通过 HTTP POST 调用
- **Redis 不可用跳过**：所有 Registrar 在 Redis 不可用时静默跳过

## 架构

```
IHostedService (启动时)
  ├─ RedisLlmRegistrar      → ILlmRegistry
  ├─ RedisRagRegistrar      → IRagRegistry
  └─ RedisSkillRegistrar    → ISkillRegistry
                                └─ RedisMockSkill (HttpEndpoint 代理)
```

## 当前状态

**已实现** — 三个 Registrar 均通过 IHostedService 自动启动。

## 源码位置

- 接口：`Agent.Engine/src/Engine/Abstractions/`
- 实现：`Agent.Engine/src/Engine/Registry/`
- 测试替身：`Agent.Engine/test/OpenAgent.Engine.Tests/TestDoubles/`
