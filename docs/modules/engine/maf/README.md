
## Feature


## 概述

MAF 是 Agent.Core 唯一生产运行时，而不是 `IAgentEngine` 的一种实现。运行时代码位于
`Backend/src/OpenAgent.Core/Runtime/Agent/`，随 Core 直接编译和注册。

## 能力

- `ChatClientAgent.RunAsync` / `RunStreamingAsync`；
- `FunctionInvokingChatClient` 管理函数循环和最大迭代；
- 平台可用能力 JSON Schema 转为 `AIFunction`；
- `ChatHistoryProvider`、`AIContextProvider` 和 `CompactionProvider`；
- 原生历史消息、附件、usage 和流式 update；
- OpenAI Chat Completions、OpenAI Responses 和 Anthropic Messages；
- 通过 MAF provider 接入平台会话锁、存储、审计、指标及 NDJSON/SSE。

## 边界

| MAF | 平台 |
|---|---|
| Agent 构造与运行 | 入口认证、租户和 Router |
| Provider `IChatClient` | 模型配置解析与授权 |
| 函数调用和结果回填 | 能力可用性解析、审计与外部执行治理 |
| 模型增量响应 | 会话锁、持久化和外部流协议 |

## 关键文件

| 文件 | 职责 |
|---|---|
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentFactory.cs` | 创建 MAF Agent 与原生 provider |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentChatClientFactory.cs` | 模型 Provider |
| `Backend/src/OpenAgent.Core/Capabilities/CapabilityToolFactory.cs` | 可用能力到 `AIFunction` 的适配 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentResponseAdapter.cs` | MAF 响应和 usage 适配 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentMessageAdapter.cs` | 消息和文件资产内容 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs` | 原生执行与 turn 边界 |

## Specification


- 每个 turn 使用授权后的 `LlmConfig` 创建 `IChatClient` 和 `ChatClientAgent`。
- `UseProvidedChatClientAsIs = true`。
- 每个 run 用 `FunctionInvokingChatClient` 包装 client。
- `MaximumIterationsPerRequest = max(1, AgentConfig.MaxTurns)`。
- 未知函数终止运行；函数连续错误阈值为零。
- 不可用能力在构建 Agent 前被过滤，不进入 MAF 工具列表。
- 工具由携带原始 `ToolDefinition` 的 `AIFunction` 执行。
- 平台 PostgreSQL 会话是唯一持久历史；对象存储保存文件原始字节。
- `AddAgentCore` 是唯一 DI 注册入口。

支持的 `ApiFormat`：

- `OpenAIChatCompletions`
- `OpenAIResponses`
- `AnthropicMessages`

消息适配支持 system/user/assistant/tool、function call/result、reasoning、文本文件和
二进制 `DataContent`。

## Design


身份和模型授权后立即创建 `ChatClientAgent` 与 `AgentSession`。平台不再构造
`ExecutionContext` 或 Engine 请求。

```text
Pipeline -> AgentRun
  -> IdentityResolution
  -> MafAgentFactory -> ChatClientAgent
       -> PlatformChatHistory : ChatHistoryProvider
       -> CapabilityToolFactory : AITool
       -> CompactionProvider
       -> FunctionInvokingChatClient
  -> Agent.Run[Streaming]Async
```

`PlatformChatHistory` 在 MAF 请求历史时加载 PostgreSQL 消息，并在 MAF
结束通知中写回成功、失败或取消状态。`CapabilityToolFactory` 发现并筛选可用能力，
直接提供携带执行体的 `AIFunction`。工具名称、描述与 schema 不再复制到
system prompt。

新增 Provider 只扩展 `IMafChatClientFactory`；新增能力只产生 `AIFunction`；新增记忆
实现只扩展 `ChatHistoryProvider`；多 Agent 编排只使用 MAF Workflow。

## Tasks


## 已完成

- [x] MAF 成为唯一生产 Agent 引擎；保留 Mock 测试引擎。
- [x] `RunAsync` 与 `RunStreamingAsync` 真实执行。
- [x] `FunctionInvokingChatClient` 接管函数调用循环和最大迭代次数。
- [x] 精确 JSON Schema 的可执行 `AIFunction` 适配器。
- [x] 工具执行回接权限、MCP、Skill、RAG、审计、遥测和持久化。
- [x] 删除 OpenAIDriver、SemanticKernel 生产实现和重复测试。
- [x] 旧 framework 配置透明映射到 MAF。
- [x] OpenAI Chat、Responses、Azure、Anthropic、Gemini、Ollama、LM Studio、OpenWebUI 客户端。
- [x] 图片、PDF 和 UTF-8 文本文件输入。
- [x] 独立文件上传、会话预览和模型文件输入。
- [x] Agent、Model、Tool、Function、MCP、Skill 六维授权扩展点。

## 后续可选增强

- [ ] 在 Host 前增加 magic-byte 检测、病毒扫描和租户对象存储。
- [ ] 对真实生产凭据执行各 Provider 的长期合约回归。
- [ ] Anthropic MAF 集成发布稳定版后移除预览包。
- [ ] 业务启用多 Agent 时再评估 MAF Workflow；当前 `Agent.Workflow` 仍不是运行服务。

## Tests


本次架构删除了 Engine 请求/响应合同，依赖这些类型的旧测试不再作为兼容要求。

后续测试直接围绕 MAF 原生边界重写：

- fake `IChatClient` 驱动 `ChatClientAgent`；
- `AIContextProvider` 能力发现与授权执行；
- `ChatHistoryProvider` 锁、历史、成功/失败写回；
- `CompactionProvider` 消息组压缩；
- `AgentSession` 工具循环与流式响应。

生产 Core 与 Engine Host 必须保持 0 warning 编译；真实 Provider、Redis、MCP E2E
单独验证。

## Conventions


## 设计约定

- 新生产能力只扩展 MAF，不再增加并行 Agent 引擎。
- `SemanticKernel`、`LangChain`、`OpenAIDriver` 只作为配置兼容值，运行时全部归一为 `MAF`。
- Provider 协议必须使用对应 SDK；Responses、Anthropic、Gemini 不伪装成 Chat Completions。
- `FunctionInvokingChatClient` 是工具循环唯一 owner；平台不得再实现第二个模型轮次循环。
- 工具声明必须保留注册表 JSON Schema，不用反射推断替代。
- 所有工具执行必须经过同一个 `ToolCallDispatcher`，确保权限、审计和取消语义一致。
- 平台会话库是唯一永久数据主责；MAF run 接收平台已加载的历史。
- Agent 不缓存，以配置热更新的正确性优先。

## 附件约定

- 图片和 PDF 以 `DataContent` 发送；文本格式严格按 UTF-8 解码。
- 文件名使用 `Path.GetFileName`，禁止路径穿越。
- 同时校验允许的扩展名、媒体类型及二者匹配关系。
- 附件原始字节不写日志、不写会话正文、不接受普通 JSON 绑定。
- Provider/模型不支持某媒体类型时，保留其明确上游错误，不伪造解析结果。

## 权限约定

- `use` 是能力是否可用的唯一判断；不可用能力不会暴露给模型。
- Tool、Function、来源资源和父级资源共同决定最终可用性，不拆分为 discover/execute 两个动作。
- 策略实现通过 `IAgentAuthorizationService` 注入；默认 allow-all 只保证升级兼容。
- 具体能力执行层负责 Provider 的连接、不可用和执行错误；MAF 只负责工具循环。

## 版本约定

- MAF 稳定包保持同一版本线。
- Anthropic 预览包必须与稳定 MAF 版本匹配并独立回归。
- 任何 Provider SDK 升级都至少运行工厂、消息、函数循环与流式测试。
