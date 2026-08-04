# MAF Runtime — 功能描述

## 概述

MAF 是 Agent.Core 唯一生产运行时，而不是 `IAgentEngine` 的一种实现。运行时代码位于
`src/Core/Runtime/Maf/`，随 Core 直接编译和注册。

## 能力

- `ChatClientAgent.RunAsync` / `RunStreamingAsync`；
- `FunctionInvokingChatClient` 管理函数循环和最大迭代；
- 平台工具 JSON Schema 转为带授权执行体的 `AIFunction`；
- `ChatHistoryProvider`、`AIContextProvider` 和 `CompactionProvider`；
- 原生历史消息、附件、usage 和流式 update；
- OpenAI Chat Completions、OpenAI Responses 和 Anthropic Messages；
- 通过 MAF provider 接入平台会话锁、存储、审计、指标及 NDJSON/SSE。

## 边界

| MAF | 平台 |
|---|---|
| Agent 构造与运行 | 入口认证、租户和 Router |
| Provider `IChatClient` | 模型配置解析与授权 |
| 函数调用和结果回填 | 工具发现、执行授权与审计 |
| 模型增量响应 | 会话锁、持久化和外部流协议 |

## 关键文件

| 文件 | 职责 |
|---|---|
| `src/Core/Runtime/Maf/MafAgentFactory.cs` | 创建 MAF Agent 与原生 provider |
| `src/Core/Runtime/Maf/MafChatClientFactory.cs` | 模型 Provider |
| `src/Core/Runtime/Maf/MafToolAdapter.cs` | 平台能力到 `AIFunction` |
| `src/Core/Runtime/Maf/MafMessageAdapter.cs` | 消息和附件 |
| `src/Core/Runtime/Maf/MafResponseReader.cs` | 原生 usage 读取 |
| `src/Core/Execution/AgentRun.cs` | 平台 turn 边界 |
