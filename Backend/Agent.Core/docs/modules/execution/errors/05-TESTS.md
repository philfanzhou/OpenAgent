# Errors — 测试计划 (TESTS)

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
