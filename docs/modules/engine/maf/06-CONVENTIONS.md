# MAF Engine — 约定

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

- `discover` 控制能力是否暴露给模型，`execute` 在真实调用前复核。
- Tool 与 Function 是两个独立维度；Skill/MCP 再按来源增加第三层检查。
- 策略实现通过 `IAgentAuthorizationService` 注入；默认 allow-all 只保证升级兼容。
- 权限异常必须原样离开 MAF 工具循环。

## 版本约定

- MAF 稳定包保持同一版本线。
- Anthropic 预览包必须与稳定 MAF 版本匹配并独立回归。
- 任何 Provider SDK 升级都至少运行工厂、消息、函数循环与流式测试。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
