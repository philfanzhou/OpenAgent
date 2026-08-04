# HealthCheck - 功能概述

## 核心用户故事

作为 Engine 运维人员，我希望 Engine 暴露标准健康检查端点，以便基础设施能监控 Engine 的可用性并做出路由决策。

## 功能简介

HealthCheck 实现三类健康检查，覆盖 Redis 连接、Agent 配置缓存和 LLM 配置可读性。通过 ASP.NET Core 健康检查框架注册，暴露 `/health`（live）和 `/ready`（ready）端点，支持 Kubernetes 等容器编排系统的探针检测。所有检查仅验证配置可读性，不发起真实外部 API 调用。

## 关键能力

- **Redis 连接检查**：验证 Redis 可用性和 Ping 响应
- **Agent 配置检查**：验证已发布 Agent 的配置缓存完整度
- **LLM 配置检查**：验证示例 Agent 的 LLM 配置可读性
- **分级健康状态**：Healthy / Degraded / Unhealthy 三级状态
- **标签分组**：infrastructure / ready / live 标签支持不同探针

## 相关文档

- [02-SPEC.md](./02-SPEC.md) - 功能规格说明
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 编码约定
- [../ConfigManagement/01-FEATURE.md](../ConfigManagement/01-FEATURE.md) - 配置管理（配置缓存）
