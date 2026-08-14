# Execution — 执行管线与核心调度

本域包含 Agent 平台的核心执行逻辑：执行入口编排、流式推理、错误处理和会话锁。

## 功能点

| 功能点 | 说明 | 详细文档 |
|--------|------|----------|
| pipeline | 执行入口与编排 | [pipeline/](./pipeline/) |
| streaming | 流式推理输出 | [streaming/](./streaming/) |
| errors | 统一错误处理与错误码 | [errors/](./errors/) |
| conversation-lock | 分布式会话锁与数据库乐观并发边界 | [conversation-lock.md](./conversation-lock.md) |

## 核心代码位置

- 执行入口：`Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs`
- 会话锁实现：`Backend/src/OpenAgent.Core/Conversation/Lock/InMemoryConversationLock.cs`
- 安全服务：`Backend/src/OpenAgent.Core/Security/`
- 错误码：`Backend/src/OpenAgent.Contracts/Requests/AgentErrorCode.cs`
- 异常类：`Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`
