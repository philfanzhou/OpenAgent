# LLM Provider 集成 — 功能描述

## 概述

所有生产模型调用通过 MAF 的 `MafChatClientFactory` 创建 `IChatClient`。平台的 `ILlmRegistry` 仍负责 Profile 注册、密钥/端点解析和 Agent 配置引用；不再存在 OpenAIDriver 或 Semantic Kernel 并行引擎。

## 支持格式

- OpenAI Chat Completions
- OpenAI Responses
- Anthropic Messages

函数调用、流式响应、多模态内容和 usage 统一通过 Microsoft.Extensions.AI / MAF 类型进入平台。

## 关键文件

| 文件 | 职责 |
|---|---|
| `src/Core/Capabilities/Llm/LlmRegistry.cs` | Profile 注册与配置解析 |
| `src/Core/Runtime/Maf/MafChatClientFactory.cs` | API 格式到 Provider client |
| `src/Core/Execution/Phases/IdentityResolution.cs` | 固定本 turn 的授权模型快照 |
| `src/Core/Runtime/Maf/MafAgentFactory.cs` | 创建 ChatClientAgent、AgentSession 扩展和函数循环配置 |

## 相关文档

- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
