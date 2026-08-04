# ConfigManagement - 功能概述

## 核心用户故事

作为 Engine 运维人员，我希望 Engine 能从 Redis 加载 Agent 配置并缓存在内存中，以便请求执行时能快速获取正确的 Agent 设置。

## 功能简介

ConfigManagement 负责 Agent 配置的加载、缓存与提供。采用三级读取链（内存快照 → Redis → Mock 降级），确保在各种可用性场景下都能返回配置。通过 `IConfigSnapshot` 实现内存缓存，`ConfigProvider` 实现读取链与降级逻辑，`EnrichWithSecureSecrets` 从环境变量注入敏感信息。

## 关键能力

- **三级读取链**：Snapshot → Redis → Mock 降级，逐级回退
- **内存缓存**：ConfigSnapshot 基于 IMemoryCache，支持按 key 或 agentId+configType 存取
- **全量配置写入**：`SetFullConfig` 一次性写入所有子配置片段及版本号
- **敏感信息注入**：从环境变量 `LLM__APIKEY` / `LLM_API_KEY` 填充 LLM API Key
- **Mock 降级**：开发/测试环境自动启用 Mock Agent，无需真实配置
- **Agent 列表查询**：从 `agent:published:index` 获取已发布 Agent 列表

## 相关文档

- [02-SPEC.md](./02-SPEC.md) - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
