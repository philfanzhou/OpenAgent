# ConfigHotReload - 功能概述

## 核心用户故事

作为 Engine 运维人员，我希望配置变更能通过 Redis Pub/Sub 实时传播到 Engine 节点，以便 Agent 配置更新无需重启服务即可生效。

## 功能简介

ConfigHotReload 通过 Redis Pub/Sub 订阅配置变更通知，实时更新内存中的 ConfigSnapshot。支持结构化 JSON 消息和传统纯文本消息两种格式；所有结构化消息统一从 Redis 全量刷新，FullSync 消息则清空快照。快照条目带 TTL，丢失 pub/sub 消息后不会永久缓存过期配置。

## 关键能力

- **Redis Pub/Sub 订阅**：监听 6 个频道（1 个当前 + 5 个遗留）
- **结构化消息处理**：ConfigUpdate 与 IncrementalUpdate 均从 Redis 全量刷新；FullSync 清空快照
- **TTL 自愈**：快照条目按 `ConfigSnapshotOptions.AbsoluteExpirationMinutes` 绝对过期，丢失消息后自动恢复
- **遗留消息兼容**：非 JSON 消息在 `agent:config:changed` 频道视为 agentId 触发全量刷新
- **即时逐出**：全量刷新时若 Redis 配置键已删除，立即从快照中移除该 agent

## 相关文档

- [02-SPEC.md](./02-SPEC.md) - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
- [../ConfigManagement/01-FEATURE.md](../ConfigManagement/01-FEATURE.md) - 配置管理功能
