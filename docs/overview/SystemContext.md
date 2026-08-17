# System Context — OpenAgent

## 服务定位

OpenAgent.Core 是 Agent 矩阵架构的核心推理库（类库，非独立部署服务），为上层服务提供 MAF Agent 执行、执行入口（AgentExecutor）、Skill/MCP/RAG、权限和会话存储能力。

## 上下游关系

```
                    ┌─────────────────┐
                    │ OpenAgent.Router │  (HTTP 入口)
                    └────────┬────────┘
                             │ 引用 OpenAgent.Core
                             ▼
                    ┌─────────────────┐
                    │ OpenAgent.Core  │  ← 本服务
                    │  (类库项目)      │
                    └────────┬────────┘
                             │ 依赖
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
     ┌────────────┐  ┌────────────┐  ┌────────────┐
     │ OpenAgent. │  │ LLM API    │  │ MCP Server │
     │ Contracts  │  │ (OpenAI等) │  │ (SSE/HTTP) │
     └────────────┘  └────────────┘  └────────────┘
              │              │              │
              ▼              ▼              ▼
     ┌────────────┐  ┌────────────┐  ┌────────────┐
     │ 共享契约    │  │ RAG 服务   │  │ 外部工具   │
     │ (Requests/ │  │ (Qdrant/   │  │ (MCP协议)  │
     │  Security/ │  │  RagFlow)  │  │            │
     │  Skills等) │  │            │  │            │
     └────────────┘  └────────────┘  └────────────┘
```

## 上游调用方

| 调用方 | 调用方式 | 典型用途 |
|--------|----------|----------|
| OpenAgent.Engine | 引用 Core 项目，通过 `AgentExecutor` | 处理来自 Router 转发的推理请求 |
| OpenAgent.Router | 转发请求到 Engine；不进入 Core 的 MAF turn | 会话亲和、限流和服务发现 |

## 下游依赖

| 依赖 | 类型 | 用途 |
|------|------|------|
| OpenAgent.Contracts | 项目引用 | 共享数据模型（Requests、Files、Rag、Routing、Authentication、Security、Skills、Configuration、Mcp、Conversation） |
| PostgreSQL (Npgsql EF Core) | NuGet | 会话、消息、文件资产及引用关系的唯一持久化存储 |
| S3-compatible object storage | HTTP/S3 API | 文件原始字节；本地使用 MinIO |
| Microsoft Agent Framework / Microsoft.Extensions.AI | NuGet | Agent、函数循环和 Provider 适配 |
| System.Net.Http | BCL | MCP Client 与外部服务通信 |

## 边界

- OpenAgent.Core 是**类库**，不独立部署，由上层 Host 服务注册和启动
- 不直接暴露 HTTP 端口，由引用方负责端点暴露
- 不直接管理进程生命周期（心跳、关闭等由 OpenAgent.Engine Host 负责）
