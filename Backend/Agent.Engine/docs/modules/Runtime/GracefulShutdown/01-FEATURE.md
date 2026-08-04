# GracefulShutdown - 功能概述

## 核心用户故事

作为 Engine 运维人员，我希望 Engine 在停机时能等待进行中的请求完成后再注销，以便部署期间不会丢失正在处理的请求。

## 功能简介

GracefulShutdown 通过跟踪进行中的请求，确保 Engine 停机时不会中断正在处理的请求。ShutdownService 维护一个进行中请求的并发字典，停机时设置关闭标志拒绝新请求，并等待所有进行中请求完成或超时。RequestScope 提供 IDisposable 包装，自动注册和完成请求。

## 关键能力

- **请求跟踪**：ConcurrentDictionary 跟踪所有进行中的请求
- **拒绝新请求**：停机时新请求抛出 AgentException(DependencyUnavailable)
- **等待完成**：停机时轮询等待进行中请求完成
- **超时保护**：超时后记录 Warning 日志，不强制终止
- **RequestScope**：IDisposable 包装，自动注册/完成请求

## 相关文档

- [02-SPEC.md](./02-SPEC.md) - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
- [../ServiceRegistration/01-FEATURE.md](../ServiceRegistration/01-FEATURE.md) - 服务注册（停机注销顺序）
