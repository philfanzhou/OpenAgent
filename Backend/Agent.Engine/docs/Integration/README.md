# Integration/ — 外部系统交互

本目录按外部系统组织 Agent.Engine 的集成点文档。每个集成点包含 6 件套（01-FEATURE ~ 06-CONVENTIONS）。

## 集成点索引

| 外部系统 | 核心用户故事 | 入口 |
|----------|-------------|------|
| [Redis](./Redis/01-FEATURE.md) | 弹性 Redis 连接与孤岛模式降级 | `IRedisConnectionProvider` |

## 6 件套说明

| 文件 | 内容 |
|------|------|
| 01-FEATURE.md | 集成概述、核心用户故事、验收条件摘要 |
| 02-SPEC.md | 详细需求（FR）、验收标准（AC）、NFR |
| 03-DESIGN.md | 接口签名、实现细节、Key 模式、配置项 |
| 04-TASKS.md | 任务清单（JSON 格式） |
| 05-TESTS.md | 测试计划、现有测试、缺失场景 |
| 06-CONVENTIONS.md | 命名约定、日志规范、错误处理模式 |
