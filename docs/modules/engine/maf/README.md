# MAF 运行时

Microsoft Agent Framework 是 Core 的唯一生产 Agent 运行时。服务与模块归属见 [开发指南](../../../overview/DevelopmentGuide.md)，请求流程见 [执行编排](../../execution/pipeline/README.md)。

## SDK 与平台边界

| SDK 管理 | 平台管理 |
|---|---|
| ChatClientAgent、AgentSession、RunAsync/RunStreamingAsync | 身份、模型配置、轮次与资源生命周期 |
| FunctionInvokingChatClient 的函数调用、结果回填与迭代 | 工具发现、权限、结果预算、超时与外部执行 |
| IChatClient 与流式 AgentResponseUpdate | SDK/平台消息转换、会话存储、文件与 HTTP/SSE |
| ChatHistoryProvider、AIContextProvider | 历史锁、持久化、压缩策略、MCP/Skill 适配 |

MAF 不替代平台权限或永久存储。PostgreSQL 是会话与文件元数据的事实源；对象存储保存文件字节。

## 运行与接入

- AgentFactory 返回 AgentExecutionScope，由其持有 Agent、历史与贡献资源，每轮创建 Agent，配置热更新后重新解析。
- 普通工具由 CapabilityToolFactory 筛选并转换为 AIFunction；MCP/Skill 通过 IAgentFeatureFactory 提供官方工具、上下文 Provider 与待释放资源，Execution 不引用具体能力。
- UseProvidedChatClientAsIs=true，模型轮次与工具回填只由 FunctionInvokingChatClient 驱动；不在平台重建第二个循环。
- MaximumIterationsPerRequest 使用正数 AgentConfig.MaxTurns，否则使用 DefaultMaxTurns=50；连续错误阈值为 3，未知工具以 tool-not-found 结果回传模型，不立即终止。
- 可用性由授权服务和来源/父级资源共同判定；执行时仍需权限检查。默认 allow-all 为兼容策略，不能据此宣称生产授权已收口。
- 平台会话由 PlatformChatHistory 加载与写回；自动压缩受 ConversationStore:EnableAutoCompaction 控制，默认关闭。

## Provider 与内容

支持 ApiFormat：OpenAIChatCompletions、OpenAIResponses、AnthropicMessages。使用对应 SDK；OpenAI 兼容服务通过其协议与 Endpoint 配置接入，不伪装不同协议。模型工厂还负责出站规格化、交互记录与压缩客户端。

Mapping 处理 system/user/assistant/tool、函数调用/结果、reasoning、文本及 DataContent。图片/PDF 按二进制内容发送，文本严格按 UTF-8 解码；文件名、扩展名、媒体类型与所有权检查在文件入口执行。不支持的媒体由 Provider 明确报错，不伪造解析结果。

MAF Agent run 接入 OpenTelemetry invoke_agent span；工具调用由 FICC 产生 execute_tool span。当前敏感内容采集开启，UseProvidedChatClientAsIs 下不自动挂接模型 chat span；交互捕获与导出边界见 [观测文档](../../../overview/Observability.md)。

## 扩展约束与验证

- 新能力扩展现有 SDK/平台接口，不新增并行 Agent 引擎或第二个 composition root；记忆实现扩展 ChatHistoryProvider。
- SemanticKernel/LangChain/OpenAIDriver 仅为旧配置兼容值，运行时归一为 MAF；未来多 Agent 编排先评估 MAF Workflow，Agent.Workflow 当前仍不是运行服务。
- 稳定 MAF 包保持同一版本线，Anthropic 预览依赖独立回归；SDK 升级验证模型工厂、消息、函数循环、流式与真实 Provider。
- 默认测试使用 fake IChatClient 驱动 Agent；覆盖历史、工具列表/调用、usage、取消及资源释放。真实模型、MCP 与 Runner 按环境单独验收，不由单元测试推断生产可用性。
- 工具 schema、错误与隔离规则见 [工具调用](../../capabilities/tool-calling/README.md)；附件与共享策略见 [文件集成](../../../integrations/README.md)，避免在运行时文档重复维护行为清单。

## 代码入口

| 路径（Backend/src/OpenAgent.Core/ 下） | 职责 |
|---|---|
| Execution/AgentFactory.cs、AgentExecutionScope.cs、AgentExecutor.cs | SDK 组合、资源与执行入口 |
| ModelProviders/AgentChatClientFactory.cs | Provider 与交互记录装饰 |
| Tooling/Discovery/CapabilityToolFactory.cs | 平台工具适配与授权筛选 |
| Capabilities/Mcp/McpFeatureFactory.cs、Capabilities/Skill/SkillFeatureFactory.cs | SDK 功能贡献 |
| Conversation/History/PlatformChatHistory.cs | 平台历史 Provider |
| Mapping/AgentMessageAdapter.cs、AgentResponseAdapter.cs | 内容、usage 与流式转换 |
