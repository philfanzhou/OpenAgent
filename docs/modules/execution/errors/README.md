
## Feature


## 核心用户故事

作为 Agent 系统的开发者和运维者，我希望执行过程中的异常被正确分类、传播和记录，以便快速定位问题、区分可恢复与不可恢复错误，并确保异常不会导致会话数据丢失。

## 功能名称和一句话概括

统一错误处理 — 定义执行层异常分类、传播规则和会话写回保障，确保异常不吞没、不丢失上下文。

## 补充约束

- Core 层定义异常语义，不越界定义宿主层响应协议
- AgentException 携带 ErrorCode，用于结构化错误分类
- 工具执行异常（非 AgentException）返回错误文本，不中断推理循环
- 取消和失败时必须写回已产生的 partial 消息
- 异常日志必须包含 TraceId/AgentId/TenantId 等追踪字段

## 关键验收条件摘要

- [ ] AgentException 携带 ErrorCode 和 Details
- [ ] Pipeline 捕获 AgentException 并转为 AgentResponse（Success=false）
- [ ] 工具执行异常返回错误文本而非抛出
- [ ] 取消时写回 partial 消息并标记 Cancelled
- [ ] 失败时写回 partial 消息并标记 Failed
- [ ] 异常日志包含足够定位问题的上下文

## 明确列出"范围外"

- HTTP 状态码映射（属宿主层）
- SSE 错误事件格式（属宿主层）
- 全局异常中间件行为（属宿主层）

## 文档索引

- [02-SPEC.md](./02-SPEC.md) — 详细需求规格
- [03-DESIGN.md](./03-DESIGN.md) — 设计说明
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试计划
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 约定与规范

## Specification


## 功能概述和用户故事

作为 Agent 系统的开发者和运维者，我希望执行过程中的异常被正确分类、传播和记录，以便快速定位问题、区分可恢复与不可恢复错误，并确保异常不会导致会话数据丢失。

## 功能要求清单

### 异常分类

- [ ] FR-01: AgentException 作为 Core 层基础异常，携带 AgentErrorCode 和 Details
- [ ] FR-02: ToolExecutionException 继承 AgentException，携带 ToolName 和 Arguments
- [ ] FR-03: HumanApprovalRequiredException 继承 AgentException，携带 ActionDescription 和 ApprovalToken
- [ ] FR-04: AudiencePermissionDeniedException 继承 AgentException，携带 DeniedAudiences 和 RequiredPermission
- [ ] FR-05: TenantDataIsolationException 继承 AgentException，携带 TenantId 和 RequestedTenantId

### AgentErrorCode 分类

- [ ] FR-06: 权限类错误（PermissionDenied: 100）
- [ ] FR-07: Skill 工具类错误（UnauthorizedSkill: 1001, SkillNotFound: 1002, SkillExecutionFailed: 1003, SkillTimeout: 1004, SkillValidationFailed: 1005, SkillQuotaExceeded: 1006）
- [ ] FR-08: MCP 类错误（McpConnectionFailed: 2001, McpToolNotFound: 2002, McpToolExecutionFailed: 2003, McpConnectionTimeout: 2004, McpServerUnavailable: 2005）
- [ ] FR-09: RAG 类错误（RagRetrievalFailed: 3001, RagIndexNotFound: 3002, RagPermissionDenied: 3003）
- [ ] FR-10: LLM 类错误（LlmProviderNotSupported: 4001, LlmConnectionFailed: 4002, LlmTimeout: 4003, LlmQuotaExceeded: 4004, LlmInvalidResponse: 4005, LlmModelNotFound: 4006）
- [ ] FR-11: 租户类错误（TenantMismatch: 5001, TenantNotFound: 5002, TenantDataIsolationViolation: 5003）
- [ ] FR-12: 受众类错误（AudiencePermissionDenied: 6001, AudienceMismatch: 6002）
- [ ] FR-13: 人工审批类错误（HumanApprovalRequired: 7001, HumanApprovalDenied: 7002, HumanApprovalTimeout: 7003）
- [ ] FR-14: 请求类错误（InvalidRequest: 8001, MissingRequiredField: 8002, InvalidIdempotencyKey: 8003）
- [ ] FR-15: 系统类错误（InternalError: 9001, PipelineExecutionFailed: 9002, ConfigurationError: 9003, DependencyUnavailable: 9004）

### Pipeline 层异常处理

- [ ] FR-16: Pipeline.ExecuteCoreAsync 捕获 AgentException，转为 AgentResponse（Success=false, ErrorCode, ErrorMessage）
- [ ] FR-17: Pipeline.ExecuteCoreAsync 捕获通用 Exception，转为 AgentResponse（Success=false, InternalError）
- [ ] FR-18: 中间件异常向上传播，Pipeline 不吞没

### Service 层异常处理

- [ ] FR-19: 工具执行异常（非 AgentException）返回错误文本字符串，不中断推理循环
- [ ] FR-20: 工具执行 AgentException 直接向上抛出
- [ ] FR-21: 非流式路径取消时写回已产生消息（Cancelled），重新抛出 OperationCanceledException
- [ ] FR-22: 非流式路径失败时写回已产生消息（Failed），重新抛出异常
- [ ] FR-23: 流式路径取消时写回 partial 消息（Cancelled），通过 ExceptionDispatchInfo 重新抛出
- [ ] FR-24: 流式路径失败时写回 partial 消息（Failed），通过 ExceptionDispatchInfo 重新抛出

### 会话写回保障

- [ ] FR-25: 取消/失败时使用 CancellationToken.None 写回，确保 partial 消息不丢失
- [ ] FR-26: 会话写回失败仅记录日志，不影响主执行结果
- [ ] FR-27: 写回时通过 PersistPartialAssistantMessage 确保未记录的 assistant 内容被写入

## 详细的验收标准

### AC-FR-16: Pipeline 捕获 AgentException
- Given: AgentRun 抛出 AgentException(PermissionDenied, "User is not authenticated")
- When: Pipeline.ExecuteAsync()
- Then: 返回 AgentResponse { Success=false, ErrorCode=PermissionDenied, ErrorMessage="User is not authenticated" }

### AC-FR-19: 工具执行异常返回错误文本
- Given: Skill 工具执行抛出 InvalidOperationException
- When: Service 执行工具
- Then: 返回 "Error executing tool: {message}"，推理继续

### AC-FR-21: 非流式取消写回
- Given: 推理过程中收到取消信号
- When: OperationCanceledException 被捕获
- Then: 写回已产生消息（Cancelled），重新抛出异常

### AC-FR-23: 流式取消写回
- Given: 流式执行过程中收到取消信号
- When: OperationCanceledException 被捕获
- Then: 写回 partial 消息（Cancelled），通过 ExceptionDispatchInfo 重新抛出

## 非功能需求

- 异常日志必须包含 TraceId、AgentId、TenantId、工具/依赖名称、失败阶段
- 不把底层实现细节直接暴露给最终用户
- 会话写入失败不应反向影响主回答结果

## 测试策略

- 单元测试覆盖：AgentException 转换、工具执行异常处理、取消/失败写回、异常传播
- 测试文件：`test/OpenAgent.Core.Tests/Conversation/AgentRunExecutionTests.cs`、`test/OpenAgent.Core.Tests/Middleware/AuthTests.cs`、`test/OpenAgent.Core.Tests/Middleware/TenantValidationTests.cs`

## Design


## 本功能在项目中的目录与文件结构

```
src/Core/
├── Impl/Pipeline.cs              # Pipeline 层异常捕获与 AgentResponse 转换
├── Impl/Service.cs               # Service 层异常处理（工具执行、取消/失败写回）
├── Security/Auth.cs              # 认证异常抛出（AgentException）
├── Security/TenantValidation.cs  # 租户校验异常抛出（TenantDataIsolationException）
├── Middleware/AuditLogging.cs     # 审计中间件异常记录

Contracts/
├── Requests/AgentErrorCode.cs    # 错误码枚举定义
├── Requests/AgentResponse.cs     # 响应模型（含 ErrorCode/ErrorMessage）
├── Security/Exceptions.cs        # 异常类定义（AgentException 及子类）
```

## 关键类型签名

```csharp
// AgentErrorCode — 错误码枚举
public enum AgentErrorCode : int
{
    Success = 0,
    PermissionDenied = 100,
    // Skill: 1001-1006
    // MCP: 2001-2005
    // RAG: 3001-3003
    // LLM: 4001-4006
    // Tenant: 5001-5003
    // Audience: 6001-6002
    // HumanApproval: 7001-7003
    // Request: 8001-8003
    // System: 9001-9004
}

// AgentException — Core 层基础异常
public class AgentException : Exception
{
    public AgentErrorCode ErrorCode { get; }
    public string? Details { get; }
}

// ToolExecutionException — 工具执行异常
public class ToolExecutionException : AgentException
{
    public string ToolName { get; }
    public Dictionary<string, object>? Arguments { get; }
}

// HumanApprovalRequiredException — 人工审批异常
public class HumanApprovalRequiredException : AgentException
{
    public string ActionDescription { get; }
    public string? ApprovalToken { get; }
}

// AudiencePermissionDeniedException — 受众权限异常
public class AudiencePermissionDeniedException : AgentException
{
    public IReadOnlyList<string> DeniedAudiences { get; }
    public string? RequiredPermission { get; }
}

// TenantDataIsolationException — 租户隔离异常
public class TenantDataIsolationException : AgentException
{
    public string? TenantId { get; }
    public string? RequestedTenantId { get; }
}

// AgentResponse — 统一响应模型
public class AgentResponse
{
    public string Content { get; set; }
    public bool Success { get; set; }
    public AgentErrorCode ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public string TraceId { get; set; }
}
```

## 依赖的数据库表/字段

无直接数据库依赖。

## 异常传播流程

### Pipeline 层（ExecuteCoreAsync）

```
Pipeline -> AgentRun.RunAsync()
  │
  ├─ 成功 → AgentResponse { Success=true, Content=... }
  │
  ├─ AgentException → AgentResponse { Success=false, ErrorCode=ex.ErrorCode, ErrorMessage=ex.Message }
  │
  └─ Exception → AgentResponse { Success=false, ErrorCode=InternalError, ErrorMessage=ex.Message }
```

### Service 层 — 工具执行

```
ExecuteToolAsync(toolName, args)
  │
  ├─ AgentException → 直接 throw（不捕获）
  │
  └─ Exception → return "Error executing tool: {ex.Message}"（返回错误文本）
```

### Service 层 — 非流式取消/失败

```
ExecuteAsync()
  │
  ├─ OperationCanceledException → SaveConversationAsync(Cancelled) → throw
  └─ Exception → SaveConversationAsync(Failed) → throw
```

### Service 层 — 流式取消/失败

```
ExecuteStreamAsync()
  │
  ├─ chunk 枚举 OperationCanceledException → terminalStatus=Cancelled, terminalException=ex
  ├─ chunk 枚举 Exception → terminalStatus=Failed, terminalException=ex
  ├─ 工具执行 OperationCanceledException → terminalStatus=Cancelled, terminalException=ex
  ├─ 工具执行 Exception → terminalStatus=Failed, terminalException=ex
  │
  └─ 流结束后:
       PersistPartialAssistantMessage()
       SaveConversationAsync(terminalStatus, CancellationToken.None)
       ExceptionDispatchInfo.Capture(terminalException).Throw()
```

## 关键设计决策

1. **Pipeline 将异常转为 AgentResponse**：对上层屏蔽异常细节；中间件异常直接传播
2. **Service 工具执行中，AgentException 直接抛出**：表示业务级错误；其他异常返回错误文本：表示可恢复的执行错误
3. **流式路径使用 terminalException/terminalStatus 模式延迟异常处理**：确保 partial 消息先写回
4. **ExceptionDispatchInfo.Capture 保留原始堆栈**：避免堆栈丢失
5. **会话写回使用 CancellationToken.None**：确保取消场景下 partial 消息不丢失

## Tasks


> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "AgentErrorCode 枚举定义（10 大类 30+ 错误码）",
    "files": ["Agent.Contracts/Requests/AgentErrorCode.cs"],
    "acceptance": "错误码覆盖权限/Skill/MCP/RAG/LLM/租户/受众/审批/请求/系统"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "AgentException 基类及 4 个子类定义",
    "files": ["Agent.Contracts/Security/Exceptions.cs"],
    "acceptance": "异常类携带 ErrorCode/Details 及特定字段"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "Pipeline 异常捕获与 AgentResponse 转换",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "AgentException 和通用异常被捕获并转为失败响应"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": [],
    "action": "Service 工具执行异常处理（AgentException 向上抛出，其他返回错误文本）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "工具执行异常不中断推理循环"
  },
  {
    "id": "TASK-05",
    "status": "implemented",
    "depends_on": [],
    "action": "Service 非流式取消/失败写回",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "取消/失败时写回已产生消息并标记状态"
  },
  {
    "id": "TASK-06",
    "status": "implemented",
    "depends_on": [],
    "action": "Service 流式取消/失败写回（terminalException 模式 + ExceptionDispatchInfo）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "流式取消/失败时写回 partial 消息并重新抛出异常"
  },
  {
    "id": "TASK-07",
    "status": "implemented",
    "depends_on": [],
    "action": "Auth 中间件抛出 AgentException(PermissionDenied)",
    "files": ["src/Core/Security/Auth.cs"],
    "acceptance": "未认证用户触发 PermissionDenied 错误"
  },
  {
    "id": "TASK-08",
    "status": "implemented",
    "depends_on": [],
    "action": "TenantValidation 中间件抛出 TenantDataIsolationException",
    "files": ["src/Core/Security/TenantValidation.cs"],
    "acceptance": "租户隔离违反触发 TenantDataIsolationViolation 错误"
  }
]
```

## Tests


测试工具：xUnit + Moq
现有测试文件：
- test/OpenAgent.Core.Tests/Conversation/AgentRunExecutionTests.cs
- test/OpenAgent.Core.Tests/Middleware/AuthTests.cs
- test/OpenAgent.Core.Tests/Middleware/TenantValidationTests.cs

## 单元测试

### UT-01 流式取消写回

- **Given**：流式执行过程中引擎抛出 OperationCanceledException
- **When**：ExecuteStreamAsync()
- **Then**：写回 user + partial assistant 消息，状态为 Cancelled

### UT-02 流式失败写回

- **Given**：流式执行过程中引擎抛出 InvalidOperationException
- **When**：ExecuteStreamAsync()
- **Then**：写回 user + partial assistant 消息，状态为 Failed

### UT-03 Auth 未认证拒绝

- **Given**：用户未认证
- **When**：Auth 中间件执行
- **Then**：抛出 AgentException(PermissionDenied)

### UT-04 TenantValidation 租户缺失拒绝

- **Given**：TenantId 缺失
- **When**：TenantValidation 中间件执行
- **Then**：抛出 AgentException

## 遗漏的测试场景

- Pipeline 捕获 AgentException 并转为 AgentResponse 的验证
- Pipeline 捕获通用 Exception 并转为 AgentResponse(InternalError) 的验证
- 工具执行异常返回错误文本而非抛出的验证
- 工具执行 AgentException 直接向上抛出的验证
- 非流式路径取消时写回消息的验证
- 非流式路径失败时写回消息的验证
- ExceptionDispatchInfo 保留原始堆栈的验证
- 会话写回失败不影响主执行结果的验证
- ToolExecutionException 携带 ToolName 和 Arguments 的验证
- HumanApprovalRequiredException / AudiencePermissionDeniedException / TenantDataIsolationException 的构造和属性验证
- 各 AgentErrorCode 值的正确范围验证

## Conventions


## 命名约定

- 异常类放在 `Agent.Contracts/Security/Exceptions.cs`，不放在 Core 实现目录
- 异常类名使用场景名 + Exception 后缀：`ToolExecutionException`、`HumanApprovalRequiredException`
- 错误码枚举放在 `Agent.Contracts/Requests/AgentErrorCode.cs`
- 错误码按类别分段：权限(100)、Skill(1xxx)、MCP(2xxx)、RAG(3xxx)、LLM(4xxx)、租户(5xxx)、受众(6xxx)、审批(7xxx)、请求(8xxx)、系统(9xxx)

## 日志约定

- Pipeline 捕获 AgentException 时记录 Error 级别：`"Agent execution failed with error code {ErrorCode}"`
- Pipeline 捕获通用异常时记录 Error 级别：`"Unexpected error during agent execution"`
- Service 取消时记录 Warning 级别：`"Agent execution cancelled for conversation {ConversationId}"`
- Service 失败时记录 Error 级别：`"Agent execution failed for conversation {ConversationId}"`
- 工具执行异常记录 Error 级别：`"Error executing tool {ToolName}"`
- 认证拒绝记录 Warning 级别：`"Unauthenticated user {UserId} attempted to access agent"`
- 审计中间件失败记录 Warning 级别：`"Audit: Request failed after {ElapsedMs}ms"`

## 异常传播约定

- Pipeline 层：AgentException 和通用 Exception 被捕获并转为 AgentResponse，中间件异常直接传播
- Service 层工具执行：AgentException 直接 throw，其他异常返回错误文本
- Service 层取消/失败：先写回消息，再重新抛出异常
- 流式路径：使用 ExceptionDispatchInfo.Capture 保留原始堆栈

## 会话写回约定

- 取消/失败时使用 CancellationToken.None 写回，确保 partial 消息不丢失
- 写回前调用 PersistPartialAssistantMessage 确保未记录的 assistant 内容被写入
- 会话写回失败仅记录 Error 日志，不影响主执行结果

## 错误码使用约定

- 新增错误码必须分配到正确的类别段
- 错误码值一旦发布不可变更
- 同一错误场景在不同层级使用相同的 ErrorCode
- AgentException.Message 默认使用 ErrorCode.ToString()，可自定义覆盖
