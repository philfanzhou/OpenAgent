# ServiceRegistration - 功能概述

## 核心用户故事

作为 Engine 运维人员，我希望 Engine 实例能自动在 Redis 中注册、发送心跳并在停机时注销，以便 Router 能够发现和路由流量到活跃的 Engine 节点。

## 功能简介

ServiceRegistration 负责 Engine 实例在分布式环境中的自动注册与发现。每个 Engine 实例启动后，会生成唯一 EngineId，将自身信息（主机、端口、负载）写入 Redis，并周期性发送心跳维持注册状态。停机时主动从 Redis 注销，确保 Router 仅将流量路由到活跃节点。

## 关键能力

- **自动注册**：应用启动后自动在 Redis 中注册 Engine 实例信息
- **心跳维持**：周期性更新注册信息（含负载指标），刷新 TTL
- **主动注销**：停机时从 Redis 删除注册信息
- **孤岛模式**：Redis 不可用时降级运行，不阻塞 Engine 启动
- **负载上报**：综合内存、GC、线程池压力计算负载值

## 相关文档

- [02-SPEC.md](./02-SPEC.md) - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
