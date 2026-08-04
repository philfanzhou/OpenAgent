# Data Models: MAF 原生工具调用

| 类型 | 所属 | 用途 |
|---|---|---|
| `CapabilityFunction` | Core internal | 授权发现后的名称、描述和 JSON Schema |
| `AIFunction` / `AITool` | Microsoft.Extensions.AI | 模型可见的原生工具与执行体 |
| `FunctionCallContent` | Microsoft.Extensions.AI | MAF 生成的函数调用 |
| `FunctionResultContent` | Microsoft.Extensions.AI | MAF 回填的函数结果 |
| `McpToolIdentity` | Core internal | runtime name 与原始 server/tool 身份绑定 |

Core 不定义 Engine message/request/result DTO。消息和工具循环全部使用 MAF 与
Microsoft.Extensions.AI 类型。
