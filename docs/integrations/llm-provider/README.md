
## Feature


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
| `Backend/src/OpenAgent.Core/Models/LlmRegistry.cs` | Profile 注册与配置解析 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentChatClientFactory.cs` | API 格式到 Provider client |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentFactory.cs` | 创建 ChatClientAgent、AgentSession 扩展和函数循环配置 |

## Specification


## 配置解析

`MafAgentProvider` 读取 `AgentConfig.Llm`，调用 `ILlmRegistry.ResolveConfig` 合并 Profile 与 Agent 局部覆盖，再传给 `IMafChatClientFactory.Create`。

```csharp
internal interface IMafChatClientFactory
{
    IChatClient Create(LlmConfig config);
}
```

必需字段为 `Format`、`ModelId`；三个云 Provider 都需要 API key，Endpoint 为空时 OpenAI 使用官方默认地址，Anthropic 使用 SDK 默认地址。密钥不得进入日志。

## 格式映射

| ApiFormat | SDK 边界 |
|---|---|
| OpenAIChatCompletions | OpenAI Chat client |
| OpenAIResponses | OpenAI Responses client |
| AnthropicMessages | MAF Anthropic provider |

## 失败语义

配置缺失或格式不支持在发出网络请求前失败；Provider HTTP、限流、模型和内容策略错误保持原异常进入平台失败路径。权限校验在 client 请求之前完成。

## Design


## 数据流

```text
AgentConfig.Llm
  -> ILlmRegistry.ResolveConfig
  -> MafChatClientFactory
       -> protocol-specific official SDK
       -> IChatClient
  -> ChatClientAgent
  -> FunctionInvokingChatClient
```

API 格式按协议分支，不共享自研 HTTP body 或 SSE parser。Responses 使用 Responses client；Anthropic 使用 Messages provider；Gemini 使用 generateContent provider；仅明确兼容 Chat Completions 的端点复用 OpenAI Chat client。

## 配置热更新

`MafAgentProvider` 不缓存 `AIAgent`。每次调用读取当前 Agent 配置并创建轻量 `ChatClientAgent`，从而与现有 ConfigProvider 热更新保持一致。

## 能力边界

平台将图片/PDF/文本、工具 Schema 和历史转换为 MEAI 内容。具体模型是否支持视觉、PDF、函数或 reasoning 由 Provider 返回明确结果；工厂不伪造能力。

## Tasks


## 已完成

- [x] LLM Profile 注册、解析和 Agent 覆盖。
- [x] 生产调用统一到 MAF。
- [x] 八种 ApiFormat 的独立 client 构造。
- [x] OpenAI Responses 使用 Responses client。
- [x] Anthropic Messages 与 Gemini generateContent 官方 SDK 适配。
- [x] 真流式、函数调用、多模态和 usage 统一映射。
- [x] 删除自研 OpenAI HTTP/SSE/重试实现和 Semantic Kernel 引擎。

## 后续维护

- [ ] Provider SDK 升级时运行在线合约测试。
- [ ] Anthropic 集成稳定版发布后替换预览包。

## Tests


旧 Engine DTO 和委托替身测试已失效。新测试应直接使用 fake `IChatClient` 验证：

- `MafChatClientFactory` 的 Provider 构造；
- `ChatClientAgent` 非流式/流式调用；
- `MafCapabilityProvider` 返回原生 `AITool`；
- `PlatformChatHistory` 的 MAF history 生命周期；
- `CompactionProvider` 策略选择；
- `AgentSession` 内的函数循环、usage 和失败传播。

真实 Provider、Redis、MCP E2E 与本地替身分开报告。

## Conventions


## 配置约定

- Provider Profile 保存共享 endpoint/key/format，Agent 保存 provider 引用、model 和必要覆盖。
- 禁止日志输出 API key、Authorization header、附件字节和完整提示内容。
- 新 Provider 格式必须扩展 `ApiFormat`、工厂、RedisTool 类型和测试。

## 协议约定

- 优先使用协议所有者或 MAF 官方集成。
- 不把 Responses、Messages、generateContent 伪装成 Chat Completions。
- OpenAI-compatible 只用于明确兼容其请求与流事件语义的服务。
- Provider 错误保留原始异常链，不返回虚假的成功回答。

## 版本约定

- MAF 稳定包保持同一版本。
- Anthropic 预览包与 MAF 稳定版本成组升级。
- SDK 升级必须验证构造、函数循环、流式、多模态和 usage。
