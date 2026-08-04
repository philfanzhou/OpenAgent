# Turn Execution — 设计说明

原 `Service` 模块已被 `AgentRun` 取代。目录名保留用于旧文档链接兼容，不代表仍存在
Service facade。

```text
Pipeline
  -> AgentRun.Run[Streaming]Async
       -> resolve authorized identity/model
       -> create ChatClientAgent + AgentSession
       -> execute one native MAF run
```

`AgentRun` 是唯一 turn 边界：

- 解析并授权 Agent、用户和模型；
- 将能力发现挂入 MAF `AIContextProvider`；
- 将会话锁与存储挂入 MAF `ChatHistoryProvider`；
- 将上下文压缩挂入 MAF `CompactionProvider`；
- 输出平台 usage 和 tool marker。

模型循环不在平台代码中。不存在 `IAgentService`、`Service`、`ExecutionInitializer`、
`ConversationExecutor`、`MafAgentBinding` 或 `IAgentEngine`。请求审计由 middleware
完成，不向 MAF provider 传递 telemetry 对象。

新增功能不得在 `Pipeline` 与 `AgentRun` 之间增加新协调层，也不得在 `AgentRun` 与
MAF 之间恢复通用 Engine Contract。
