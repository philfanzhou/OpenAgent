# Execution — 执行管线与核心调度

本域包含 Agent.Core 的核心执行逻辑：Pipeline 中间件链、Service 业务编排、流式推理、错误处理、分布式会话锁。

## 功能点

| 功能点 | 说明 | 详细文档 |
|--------|------|----------|
| pipeline | Pipeline 中间件链执行 | [pipeline/](./pipeline/) |
| service | Agent 业务编排（工具调用循环、会话管理） | [service/](./service/) |
| streaming | 流式推理输出 | [streaming/](./streaming/) |
| errors | 统一错误处理与错误码 | [errors/](./errors/) |
| conversation-lock | 分布式会话锁（Redis SET NX EX + Lua + 心跳） | [conversation-lock.md](./conversation-lock.md) |

## 核心代码位置

- Pipeline 实现：`src/Core/Execution/Pipeline.cs`
- Service 实现：`src/Core/Execution/Service.cs`
- 会话锁实现：`src/Core/Conversation/Lock/RedisConversationLock.cs`、`src/Core/Conversation/Lock/InMemoryConversationLock.cs`
- 中间件：`src/Core/Middleware/`、`src/Core/Security/`
- 错误码：`Agent.Contracts/Requests/AgentErrorCode.cs`
- 异常类：`Agent.Contracts/Security/Exceptions.cs`
