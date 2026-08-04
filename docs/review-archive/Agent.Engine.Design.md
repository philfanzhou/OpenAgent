# Agent.Engine — 设计摘要 (Design)

> 本文件只保留服务级架构概述和统一设计约束。详细设计见 [modules/](../modules/README.md) 各功能点的 03-DESIGN.md。

## 1. 分层架构

```
┌─────────────────────────────────────────────┐
│                  Host 层                      │
│  Program.cs / EndpointExtensions / Middleware │
│  职责：端点暴露、认证、异常处理、流式协议      │
├─────────────────────────────────────────────┤
│                Engine 运行时层                │
│  RedisRegistry / ConfigProvider / HotReload   │
│  HeartbeatService / ShutdownService           │
│  职责：注册发现、配置管理、热加载、优雅停机    │
├─────────────────────────────────────────────┤
│                Core 执行层                    │
│  Pipeline / Service / EngineFactory           │
│  Skill / MCP / RAG / LLM 编排                 │
│  职责：智能体执行、工具编排、运行时鉴权        │
├─────────────────────────────────────────────┤
│              Infrastructure                  │
│  Redis / Agent.Hosting / 外部 API             │
│  职责：存储、认证、可观测性、外部调用          │
└─────────────────────────────────────────────┘
```

## 2. 技术栈

| 类别 | 技术 | 版本 |
|------|------|------|
| 运行时 | .NET | 8.0 |
| Web 框架 | ASP.NET Core | 8.0 |
| Redis 客户端 | StackExchange.Redis | 2.9.11 |
| 认证 | JWT Bearer (via Agent.Hosting) | - |
| 可观测性 | OpenTelemetry (via Agent.Hosting) | - |
| 测试框架 | xUnit + Moq | - |
| 序列化 | System.Text.Json | - |

## 3. 项目依赖关系

```
OpenAgent.Core.Engine.Host
  ├── OpenAgent.Engine
  │     └── OpenAgent.Core (Core)
  ├── OpenAgent.Core (Core)
  └── OpenAgent.Hosting
```

## 4. 统一设计约束

1. **宿主与执行分离**：Engine 负责进程托管，Core 负责智能体执行
2. **进程内无持久业务状态**：外部状态位于 Redis 和配置中心
3. **契约优先**：端点、健康检查、流式协议必须与代码一致
4. **故障可降级**：Redis 不可用时进入孤岛模式，不阻断本地请求
5. **内部类优先**：除接口实现外，类默认 `internal`，方法默认 `private`
6. **文件作用域命名空间**：所有 .cs 文件使用 `namespace X;` 格式

## 5. 详细设计下钻

| 功能域 | 详细设计文档 |
|--------|-------------|
| 服务注册与发现 | [Runtime/ServiceRegistration/03-DESIGN.md](../modules/Runtime/ServiceRegistration/03-DESIGN.md) |
| 配置管理 | [Runtime/ConfigManagement/03-DESIGN.md](../modules/Runtime/ConfigManagement/03-DESIGN.md) |
| 配置热加载 | [Runtime/ConfigHotReload/03-DESIGN.md](../modules/Runtime/ConfigHotReload/03-DESIGN.md) |
| 优雅停机 | [Runtime/GracefulShutdown/03-DESIGN.md](../modules/Runtime/GracefulShutdown/03-DESIGN.md) |
| 健康检查 | [Runtime/HealthCheck/03-DESIGN.md](../modules/Runtime/HealthCheck/03-DESIGN.md) |
| 能力注册 | [Runtime/CapabilityRegistration/03-DESIGN.md](../modules/Runtime/CapabilityRegistration/03-DESIGN.md) |
| 聊天 API | [Host/ChatApi/03-DESIGN.md](../modules/Host/ChatApi/03-DESIGN.md) |
| 错误处理 | [Host/ErrorHandling/03-DESIGN.md](../modules/Host/ErrorHandling/03-DESIGN.md) |
| Redis 集成 | [Integration/Redis/03-DESIGN.md](../Integration/Redis/03-DESIGN.md) |
