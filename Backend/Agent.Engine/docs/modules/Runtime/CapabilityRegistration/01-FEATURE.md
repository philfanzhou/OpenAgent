# CapabilityRegistration - 功能概述

## 核心用户故事

作为 Engine 运维人员，我希望 Engine 启动时能从 Redis 加载 LLM/RAG/Skill 能力配置，以便这些能力在 Agent 执行时可用。

## 功能简介

CapabilityRegistration 在 Engine 启动时从 Redis 加载三类能力配置：LLM Provider Profile、RAG Instance Config 和 Skill Instance Config。三个 Registrar 均实现 IHostedService，在 StartAsync 中同步读取 Redis 索引和详细配置，通过各自的 Registry 注册到内存中。RedisSkillRegistrar 创建 RedisMockSkill 代理，支持 HttpEndpoint 类型 Skill 的远程调用。

## 关键能力

- **LLM 能力注册**：从 `llm:published:index` 加载 LlmProviderProfile
- **RAG 能力注册**：从 `rag:published:index` 加载 RagInstanceConfig
- **Skill 能力注册**：从 `skill:published:index` 加载 SkillInstanceConfig，创建 RedisMockSkill 代理
- **HttpEndpoint Skill 执行**：RedisMockSkill 对 HttpEndpoint 类型 Skill 通过 HTTP POST 调用远程端点
- **Redis 不可用跳过**：所有 Registrar 在 Redis 不可用时静默跳过

## 相关文档

- [02-SPEC.md](./02-SPEC.md) - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
