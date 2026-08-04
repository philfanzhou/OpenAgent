# Errors — 约定与规范 (CONVENTIONS)

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
