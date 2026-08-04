
## Feature


## 核心用户故事

作为上层服务，我希望通过 Pipeline 执行 Agent 请求，以便在核心业务逻辑前后插入认证、审计、追踪等横切关注点。

## 功能名称和一句话概括

Pipeline 中间件链 — 按注册顺序执行中间件，最终调用 AgentService 完成推理。

## 补充约束

- 中间件按注册顺序执行（先注册先执行前置逻辑，后执行后置逻辑）
- 流式和非流式共享同一组中间件，各自实现 InvokeAsync 和 InvokeStreamAsync
- Pipeline 不负责业务逻辑，仅负责中间件编排

## 关键验收条件摘要

- [ ] 请求经过所有注册中间件后到达 Service
- [ ] 中间件异常向上传播，不吞没
- [ ] 流式请求正确传递 CancellationToken
- [ ] AgentException 被捕获并转换为 AgentResponse（Success=false）

## 明确列出"范围外"

- 不负责具体中间件实现逻辑
- 不负责中间件注册顺序（由 DI 容器决定）

## 文档索引

- [02-SPEC.md](./02-SPEC.md) — 详细需求规格
- [03-DESIGN.md](./03-DESIGN.md) — 设计说明
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试计划
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 约定与规范

## Specification


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

## Design


`Pipeline` 只构建入口 middleware 委托链，并把核心执行直接交给 `AgentRun`。

```text
AgentRequest
  -> AgentIdValidation
  -> TenantValidation
  -> Tracing
  -> Auth
  -> AuditLogging
  -> AgentRun.Run[Streaming]Async
```

非流式路径把异常映射为 `AgentResponse`；流式路径保留异常，由 Host 的流协议边界映射。
不得在 Pipeline 与 AgentRun 之间增加 Service facade。

## Tasks


> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "Pipeline 委托链构建与执行",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "中间件按注册顺序执行，核心逻辑在最后调用"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": [],
    "action": "异常转换（AgentException → AgentResponse, Exception → AgentResponse）",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "AgentException 转为对应 ErrorCode，其他异常转为 InternalError"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": [],
    "action": "流式执行委托链构建",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "流式请求正确传播 CancellationToken 和 chunks"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": [],
    "action": "BuildContext 合并 AgentRequest 和 IAgentUserContext",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "UserId, TenantId, Roles, Groups, Claims, Audience, TraceId, ConversationId, AgentId 正确传递"
  }
]
```

## Tests


测试工具：xUnit + Moq
现有测试文件：test/OpenAgent.Core.Tests/Pipeline/PipelineExecutionTests.cs

## 单元测试

### UT-01 Pipeline 正确执行中间件链

- **Given**：注册了多个中间件
- **When**：调用 ExecuteAsync
- **Then**：中间件按注册顺序执行

### UT-02 Pipeline 捕获 AgentException

- **Given**：Service 抛出 AgentException
- **When**：调用 ExecuteAsync
- **Then**：返回 AgentResponse { Success=false, ErrorCode=ex.ErrorCode }

### UT-03 Pipeline 捕获非 AgentException

- **Given**：Service 抛出 InvalidOperationException
- **When**：调用 ExecuteAsync
- **Then**：返回 AgentResponse { Success=false, ErrorCode=InternalError }

## 遗漏的测试场景

- 流式执行中间件链测试
- CancellationToken 取消传播测试
- 空中间件列表时的直接执行测试

## Conventions


## 命名约定

- 中间件类名使用名词（如 AuditLogging, Tracing, Auth）
- 中间件 Name 属性返回类名（`nameof(ClassName)`）
- 委托类型：`AgentPipelineDelegate`、`AgentStreamPipelineDelegate`

## 日志和安全要求

- Pipeline 入口记录 Query、TraceId、UserId
- Pipeline 出口记录 Success、ErrorCode
- 中间件异常不吞没，向上传播

## 错误消息格式约定

| 场景 | 消息文本 |
|------|----------|
| AgentException | 继承原始异常的 ErrorCode 和 Message |
| 非 AgentException | ErrorCode=InternalError, Message=ex.Message |
