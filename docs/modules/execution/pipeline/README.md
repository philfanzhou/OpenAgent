# 执行编排

AgentExecutor 提供同步 ExecuteAsync 和流式 ExecuteStreamingAsync，接收 AgentRequest、IAgentUserContext 与 CancellationToken。代码目录和扩展方式见 [开发指南](../../../overview/DevelopmentGuide.md)。

## 请求流程

1. Engine.Host 通过现有 Hosting 认证/错误管道、AgentUserContextMiddleware 与 EngineAdmissionMiddleware 建立身份并执行入口治理。
2. ConversationAgentResolver 选择 Agent，IAgentRuntimeResolver 获取授权后的 Agent 配置和模型，创建 TurnContext。
3. 带附件时先确保会话，再通过 FileAssetRequestResolver 校验和解析文件。
4. AgentFactory 创建拥有 Agent、历史与贡献资源的 AgentExecutionScope；创建 AgentSession 和 user 消息后调用 AIAgent.RunAsync 或 RunStreamingAsync。
5. Mapping 转换响应/usage；流式过程记录内容、reasoning、工具调用/结果与计划事件，结束时写回历史及用量。作用域释放持有的资源。

完整 SDK 运行边界见 [MAF](../../engine/maf/README.md)，历史与锁见 [会话文档](../../conversation/README.md)。

## 异常、取消与协议

- AgentExecutor 不把异常转成 Success=false 的 AgentResponse，异常向上传播；工具调用的局部错误由工具机制隔离，规则见 [异常处理](../errors/README.md)。
- 共享 Hosting/Errors/AgentExceptionHandling.cs 负责 HTTP ProblemDetails 映射。Engine.Host/EngineErrorHandling.cs 添加 Provider 错误与 SSE 文案；响应已开始时写 error/done，客户端断开按取消处理。
- Core 返回 IAsyncEnumerable<AgentStreamEvent>；Host 的 AgentStreamWriter 将其写成 SSE。使用实际 CancellationToken，成功终态 usage 不根据文本估算。
- 取消/失败时 PlatformChatHistory 在释放期间使用 CancellationToken.None 写回 partial。初始化中途失败清理已创建的功能资源，并保留原始异常。
- HTTP 中间件顺序由 Host Program 配置；执行域不定义或注册 Web 中间件。请求日志、Trace 与排障入口见 [观测文档](../../../overview/Observability.md)。

## 验证入口

| 场景 | 对应测试目录/文件（Backend/tests/ 下） |
|---|---|
| 工具循环、Skill 工具列表、模型与 usage | OpenAgent.Core.Tests/Execution/AgentExecutor*Tests.cs |
| 功能贡献、成功释放、失败清理 | OpenAgent.Core.Tests/Execution/AgentFeatureCompositionTests.cs |
| 历史、取消 partial 与锁 | OpenAgent.Core.Tests/Conversation/History/、Lock/ |
| 错误映射与 SSE 终态 | OpenAgent.Hosting.Tests/、OpenAgent.Engine.Tests/Hosting/ |

验证身份与授权、同步/流式输出、取消、工具配对、失败清理和 HTTP/SSE 边界；不对已移交 Host 的异常转换重复创建 Core 测试。

## 代码入口

- Core：Backend/src/OpenAgent.Core/Execution/AgentExecutor.cs、AgentFactory.cs、AgentExecutionScope.cs。
- Host：Backend/src/OpenAgent.Engine.Host/Program.cs、EngineErrorHandling.cs、Extensions/AgentChatEndpointExtensions.cs、AgentStreamWriter.cs。
- HTTP 错误：Backend/src/OpenAgent.Hosting/Errors/AgentExceptionHandling.cs。
