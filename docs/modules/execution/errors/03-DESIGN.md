# Errors — 设计说明 (DESIGN)

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
