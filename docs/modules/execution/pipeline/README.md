
## Feature


## 核心用户故事

作为上层服务，我希望通过 AgentExecutor 执行 Agent 请求，由 Engine.Host 中间件链处理认证、租户校验和异常映射等横切关注点。

## 功能名称和一句话概括

执行入口 AgentExecutor — 编排 `FileAssetRequestResolver → ConversationAgentResolver → AgentFactory.CreateAsync → AIAgent.Run[Streaming]Async`。

## 补充约束

- 流式和非流式入口分别为 `ExecuteAsync` / `ExecuteStreamingAsync`，共享同一组 resolver 与工厂编排
- 横切关注点（认证、租户校验、异常映射）由 Engine.Host 的 ASP.NET Core 中间件承担，AgentExecutor 不实现中间件抽象
- AgentExecutor 仅负责执行编排，业务逻辑由 MAF Agent 与各 CapabilitySource 承担

## 关键验收条件摘要

- [ ] 请求经 Engine.Host 中间件链后到达 AgentExecutor
- [ ] 异常由 AgentExceptionHandlerMiddleware 映射为 HTTP 错误响应
- [ ] 流式请求正确传递 CancellationToken
- [ ] AgentException 被捕获并转换为 AgentResponse（Success=false）

## 明确列出"范围外"

- 不负责 Engine.Host 中间件实现逻辑
- 不负责中间件注册顺序（由 DI 容器决定）

## Specification


## 功能概述和用户故事

作为上层服务，我希望通过 AgentExecutor 执行 Agent 请求，由 Engine.Host 中间件链处理横切关注点。

## 功能要求清单

- [x] FR-01: ExecuteAsync 接收 AgentRequest + IAgentUserContext，返回 AgentResponse
- [x] FR-02: ExecuteStreamingAsync 接收 AgentRequest + IAgentUserContext，返回 IAsyncEnumerable<AgentStreamEvent>
- [x] FR-04: 核心执行直接调用 AIAgent.RunAsync/RunStreamingAsync
- [ ] FR-05: AgentException 捕获后转换为 AgentResponse（Success=false, ErrorCode, ErrorMessage）
- [ ] FR-06: 非 AgentException 捕获后转换为 AgentResponse（Success=false, InternalError）
- [x] FR-08: 流式执行正确传播 CancellationToken

## 详细的验收标准

### AC-FR-01
- Given: 已注入 resolver 与工厂
- When: 调用 AgentExecutor.ExecuteAsync(request, userContext, ct)
- Then: 请求经编排后调用 AIAgent.RunAsync，返回 AgentResponse

### AC-FR-05
- Given: 执行过程抛出 AgentException
- When: AgentExecutor.ExecuteAsync 执行
- Then: 返回 AgentResponse { Success=false, ErrorCode=ex.ErrorCode, ErrorMessage=ex.Message }

### AC-FR-06
- Given: 执行过程抛出非 AgentException
- When: AgentExecutor.ExecuteAsync 执行
- Then: 返回 AgentResponse { Success=false, ErrorCode=InternalError }

## 非功能需求

- 日志记录请求开始和完成
- TraceId 从 request.TraceId 或 Activity.Current 获取

## 测试策略

- 单元测试验证 AgentExecutor 异常转换逻辑
- 测试文件：`Backend/tests/OpenAgent.Core.Tests/`

## Design


`AgentExecutor` 是执行入口，编排 `FileAssetRequestResolver → ConversationAgentResolver → IAgentRuntimeResolver → AgentFactory.CreateAsync → AIAgent.Run[Streaming]Async`。横切关注点由 Engine.Host 的 ASP.NET Core 中间件承担。

```text
AgentRequest
  -> AgentUserContextMiddleware
  -> EngineAdmissionMiddleware
  -> AgentExceptionHandlerMiddleware
  -> AgentExecutor.Execute[Streaming]Async
```

非流式路径把异常映射为 `AgentResponse`；流式路径保留异常，由 Host 的流协议边界映射。

## Tasks


> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "AgentExecutor 执行编排",
    "files": ["Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs"],
    "acceptance": "按 resolver/工厂顺序编排，最终调用 AIAgent.Run[Streaming]Async"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": [],
    "action": "异常转换（AgentException → AgentResponse, Exception → AgentResponse）",
    "files": ["Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs"],
    "acceptance": "AgentException 转为对应 ErrorCode，其他异常转为 InternalError"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": [],
    "action": "流式执行入口构建",
    "files": ["Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs"],
    "acceptance": "流式请求正确传播 CancellationToken 和 AgentStreamEvent"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": [],
    "action": "AgentExecutor 接收并传递 AgentRequest 和 IAgentUserContext",
    "files": ["Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs"],
    "acceptance": "UserId, TenantId, Roles, Groups, Claims, Audience, TraceId, ConversationId, AgentId 正确传递"
  }
]
```

## Tests


测试工具：xUnit + Moq
现有测试文件：`Backend/tests/OpenAgent.Core.Tests/`

## 单元测试

### UT-01 AgentExecutor 正确编排执行

- **Given**：resolver 与工厂已注入
- **When**：调用 ExecuteAsync
- **Then**：按顺序调用 resolver、工厂与 AIAgent.RunAsync

### UT-02 AgentExecutor 捕获 AgentException

- **Given**：执行过程抛出 AgentException
- **When**：调用 ExecuteAsync
- **Then**：返回 AgentResponse { Success=false, ErrorCode=ex.ErrorCode }

### UT-03 AgentExecutor 捕获非 AgentException

- **Given**：执行过程抛出 InvalidOperationException
- **When**：调用 ExecuteAsync
- **Then**：返回 AgentResponse { Success=false, ErrorCode=InternalError }

## 遗漏的测试场景

- 流式执行编排测试
- CancellationToken 取消传播测试

## Conventions


## 命名约定

- 执行入口类名固定为 `AgentExecutor`
- Engine.Host 中间件类名使用名词（如 AgentUserContextMiddleware、EngineAdmissionMiddleware、AgentExceptionHandlerMiddleware）

## 日志和安全要求

- AgentExecutor 入口记录 Query、TraceId、UserId
- AgentExecutor 出口记录 Success、ErrorCode
- 异常不吞没，向上传播

## 错误消息格式约定

| 场景 | 消息文本 |
|------|----------|
| AgentException | 继承原始异常的 ErrorCode 和 Message |
| 非 AgentException | ErrorCode=InternalError, Message=ex.Message |
