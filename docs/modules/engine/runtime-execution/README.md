# 运行时

聊天推理由运行时驱动（`IAgentRuntimeDriver`）执行，平台不再内置 Agent 推理循环。
Microsoft Agent Framework（MAF）已在 PR #234 移除；实现边界见 [OpenCode 运行时](../../../integrations/opencode-runtime.md)。
服务与模块归属见 [开发指南](../../../overview/DevelopmentGuide.md)，请求流程见 [执行编排](../../execution/pipeline/README.md)。

## SDK 与平台边界

| 驱动管理 | 平台管理 |
|---|---|
| harness 进程与 ACP 会话、消息与工具的协议往返 | 身份、模型配置、轮次与资源生命周期 |
| 沙箱隔离（harness 自带机制） | 工具发现、权限、结果预算、超时与外部执行 |
| 外部 MCP server 的原生连接 | MCP 配置目录与 Agent 绑定关系 |
| skill 包的原生发现 | 平台会话历史、持久化与压缩策略 |

平台不复制推理协议或工具执行逻辑，也不存在第二条执行路径。PostgreSQL 是会话与文件元数据的事实源；对象存储保存文件字节。

## 运行与接入

- 端点直接注入 `RuntimeAgentExecutor` 作为稳定入口：解析运行配置、准备历史与工具、调用驱动、落库事件。
- `RuntimeInvocation` 携带本轮的全部输入（配置、轮次上下文、指令、消息、工具、skill、MCP server）；驱动只看到已授权的输入，不暴露协议类型。
- 平台工具经 `CapabilityToolFactory` 筛选并转换为驱动可调用的工具；`RuntimeTool.InvokeAsync` 绑定本轮身份，Agent 不能在参数中更换租户或用户。
- 工具独占并发门由 `RuntimePreparationScope` 持有，超时与结果预算由 `IsolatedToolFunction` 包装统一执行；MCP/Skill 通过 `IAgentFeatureFactory` 贡献定义与待释放资源，Execution 不引用具体能力。
- 平台会话由 `PlatformChatHistory` 加载与写回；自动压缩受 `ConversationStore:EnableAutoCompaction` 控制，默认关闭。

## Provider 与内容

支持 ApiFormat：OpenAIChatCompletions、OpenAIResponses、AnthropicMessages。使用对应 SDK；OpenAI 兼容服务通过其协议与 Endpoint 配置接入，不伪装不同协议。模型工厂还负责出站规格化、交互记录与压缩客户端。

Mapping 处理 system/user/assistant/tool、函数调用/结果、reasoning、文本及 DataContent。图片/PDF 按二进制内容发送，文本严格按 UTF-8 解码；文件名、扩展名、媒体类型与所有权检查在文件入口执行。不支持的媒体由 Provider 明确报错，不伪造解析结果。

交互捕获与导出边界见 [观测文档](../../../overview/Observability.md)。

## 扩展约束与验证

- 新能力扩展现有驱动/平台接口，不新增并行 Agent 引擎或第二个 composition root。
- 真实模型、MCP 与 Runner 按环境单独验收，不由单元测试推断生产可用性。
- 工具 schema、错误与隔离规则见 [工具调用](../../capabilities/tool-calling/README.md)；附件与共享策略见 [文件集成](../../../integrations/README.md)，避免在运行时文档重复维护行为清单。

## 代码入口

| 路径（Backend/src/OpenAgent.Application/ 下） | 职责 |
|---|---|
| Execution/RuntimeAgentExecutor.cs | 端点稳定入口与平台执行协调 |
| Execution/RuntimePreparation.cs、RuntimePreparationScope.cs | 本轮历史、工具、特性与资源准备 |
| ModelProviders/AgentChatClientFactory.cs | Provider 与交互记录装饰 |
| Tooling/Discovery/CapabilityToolFactory.cs | 平台工具适配与授权筛选 |
| Capabilities/Mcp/McpFeatureFactory.cs、Capabilities/Skill/SkillFeatureFactory.cs | 能力定义贡献 |
| Conversation/History/PlatformChatHistory.cs | 平台历史 |
| Mapping/AgentMessageAdapter.cs、AgentResponseAdapter.cs | 内容、usage 与流式转换 |
