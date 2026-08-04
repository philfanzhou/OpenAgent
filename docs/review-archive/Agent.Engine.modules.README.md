# modules/ — 内部业务能力

本目录按业务域组织 Agent.Engine 的功能点文档。每个功能点包含 6 件套（01-FEATURE ~ 06-CONVENTIONS）。

## 业务域索引

### Runtime 域

Engine 运行时能力：注册发现、配置管理、热加载、停机、健康检查、能力注册。

| 功能点 | 核心用户故事 | 入口 |
|--------|-------------|------|
| [ServiceRegistration](./Runtime/ServiceRegistration/01-FEATURE.md) | Engine 实例自动注册/心跳/注销 | `IEngineRegistry` |
| [ConfigManagement](./Runtime/ConfigManagement/01-FEATURE.md) | 从 Redis 加载配置并缓存到内存 | `IAgentConfigProvider` |
| [ConfigHotReload](./Runtime/ConfigHotReload/01-FEATURE.md) | 通过 Redis Pub/Sub 实时更新配置 | `HotReloadService` |
| [GracefulShutdown](./Runtime/GracefulShutdown/01-FEATURE.md) | 停机时等待进行中请求完成 | `ShutdownService` |
| [HealthCheck](./Runtime/HealthCheck/01-FEATURE.md) | 暴露标准健康检查端点 | `IHealthCheck` 实现 |
| [CapabilityRegistration](./Runtime/CapabilityRegistration/01-FEATURE.md) | 启动时加载 LLM/RAG/Skill 能力 | `IHostedService` 实现 |

### Host 域

Web API 宿主能力：端点暴露、错误处理。

| 功能点 | 核心用户故事 | 入口 |
|--------|-------------|------|
| [ChatApi](./Host/ChatApi/01-FEATURE.md) | 暴露同步/流式聊天 API | `EndpointExtensions` |
| [ErrorHandling](./Host/ErrorHandling/01-FEATURE.md) | 全局异常处理与 SSE 错误收口 | 中间件管线 |

## 6 件套说明

每个功能点目录包含以下文件：

| 文件 | 内容 |
|------|------|
| 01-FEATURE.md | 功能概述、核心用户故事、验收条件摘要、范围外 |
| 02-SPEC.md | 详细需求（FR）、验收标准（AC Given-When-Then）、NFR |
| 03-DESIGN.md | 文件结构、接口签名、数据依赖、调用链 |
| 04-TASKS.md | 任务清单（JSON 格式，含状态和依赖） |
| 05-TESTS.md | 测试计划（Given-When-Then）、现有测试、缺失场景 |
| 06-CONVENTIONS.md | 命名约定、日志规范、错误消息格式 |
