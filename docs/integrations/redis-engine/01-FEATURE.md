# 01-FEATURE — Redis 连接管理

## 核心用户故事

**作为** Engine 服务，**我希望**拥有一个具备自动重试和孤岛模式降级的弹性 Redis 连接，**以便**在 Redis 不可用时仍能继续处理本地请求。

## 功能概述

Engine 服务通过 Redis 实现配置热加载、引擎注册/心跳、技能/LLM/RAG 注册发现以及 Pub/Sub 配置变更通知。Redis 连接管理层提供两种连接提供者——基于 StackExchange.Redis 的生产实现和基于原始 TCP/RESP 协议的轻量测试实现——并统一通过 `IRedisConnectionProvider` 接口暴露，确保在 Redis 不可用时系统能以"孤岛模式"安全降级。

## 关键能力

| 能力 | 说明 |
|------|------|
| 弹性连接 | 连接失败后自动重试（5 秒间隔），不阻塞启动 |
| 孤岛模式降级 | Redis 不可用时，读操作返回空值/null，写操作返回 false，服务不中断 |
| 单一实现策略 | 生产环境使用 StackExchange.Redis；测试使用 FakeRedisConnectionProvider（内存 Mock） |
| 连接生命周期事件 | 监听 ConnectionFailed / ConnectionRestored / ErrorMessage 事件并记录日志 |
| 统一接口 | 生产使用 `RedisConnectionProvider`，测试使用 `FakeRedisConnectionProvider`，均实现 `IRedisConnectionProvider` |
| 健康检查 | 通过 `RedisHealthCheck` 暴露 Degraded / Healthy / Unhealthy 三级状态 |

## 相关文档

- [02-SPEC.md](./02-SPEC.md) — 功能规格与验收标准
- [03-DESIGN.md](./03-DESIGN.md) — 设计与架构
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试用例
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 编码约定
