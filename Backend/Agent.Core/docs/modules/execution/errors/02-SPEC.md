# Errors — 详细需求规格 (SPEC)

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
