# Pipeline — 详细需求规格 (SPEC)

## 功能概述和用户故事

作为上层服务，我希望通过 Pipeline 执行 Agent 请求，以便在核心业务逻辑前后插入横切关注点。

## 功能要求清单

- [ ] FR-01: ExecuteAsync 接收 AgentRequest + IAgentUserContext，返回 AgentResponse
- [ ] FR-02: ExecuteStreamAsync 接收 AgentRequest + IAgentUserContext，返回 IAsyncEnumerable<string>
- [ ] FR-03: 按中间件注册逆序构建委托链（最后注册的中间件最靠近核心）
- [x] FR-04: 核心执行直接调用 AgentRun.RunAsync/RunStreamingAsync
- [ ] FR-05: AgentException 捕获后转换为 AgentResponse（Success=false, ErrorCode, ErrorMessage）
- [ ] FR-06: 非 AgentException 捕获后转换为 AgentResponse（Success=false, InternalError）
- [ ] FR-07: 将 AgentRequest + IAgentUserContext 合并为 context 字典传递给 Service
- [ ] FR-08: 流式执行正确传播 CancellationToken

## 详细的验收标准

### AC-FR-01
- Given: 已注册中间件和 AgentService
- When: 调用 Pipeline.ExecuteAsync(request, userContext, ct)
- Then: 请求经过所有中间件后到达 Service，返回 AgentResponse

### AC-FR-05
- Given: Service 抛出 AgentException
- When: Pipeline.ExecuteAsync 执行
- Then: 返回 AgentResponse { Success=false, ErrorCode=ex.ErrorCode, ErrorMessage=ex.Message }

### AC-FR-06
- Given: Service 抛出非 AgentException
- When: Pipeline.ExecuteAsync 执行
- Then: 返回 AgentResponse { Success=false, ErrorCode=InternalError }

## 非功能需求

- 日志记录请求开始和完成
- TraceId 从 request.TraceId 或 Activity.Current 获取

## 测试策略

- 单元测试验证中间件链顺序
- 单元测试验证异常转换逻辑
- 测试文件：`test/OpenAgent.Core.Tests/Pipeline/PipelineExecutionTests.cs`
