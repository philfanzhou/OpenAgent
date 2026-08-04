# Agent.Contracts 详细设计文档

## 1. 架构概览
`Agent.Contracts` 是 Agent 矩阵架构的底层契约层，作为独立纯净的类库 (`.dll`)，没有任何具体业务实现或重量级第三方依赖。它为整个企业内外部系统提供一套统一的数据流转、外部接入与异常定义的标准规范。

## 2. 核心类与数据结构

### 2.1 ISkill / IAsyncSkill 接口
定义标准技能的执行方法。
- **ISkill**: 同步风格技能接口，`ExecuteAsync` 返回字符串结果
- **IAsyncSkill**: 异步风格技能接口，支持完整的 `SkillContext` 和 `SkillResult`
- **SkillContext**: 传递给技能执行的上下文信息，包含参数、用户上下文、TraceId、TenantId
- **SkillResult**: 技能执行结果，包含成功状态、输出、错误信息、错误码

### 2.2 IMcpClient 接口
定义 MCP 协议的标准调用规范。
- **McpTool**: MCP 工具定义，包含名称、描述、Schema、是否为危险操作
- 支持 `ConnectAsync`、`DisconnectAsync`、`ListToolsAsync`、`CallToolAsync`、`ReadResourceAsync`
- `IsConnected` 属性用于检查连接状态

### 2.3 AgentRequest / AgentResponse DTOs
跨越 Router -> Core -> Workflow 边界传递的标准数据结构。
- **AgentRequest**: 统一封装 Query、TraceId、ClientType、IdempotencyKey、Context、EnabledSkills、Parameters
- **AgentResponse**: 统一封装回答文本、引用来源（Citations）、工具调用日志（ToolCalls）、Token 使用量
- **Citation**: RAG 检索结果的来源引用
- **ToolCallLog**: 工具调用记录，包含名称、参数、结果、耗时
- **TokenUsage**: Token 统计信息

### 2.4 IAgentUserContext 接口
标准化的用户身份和权限上下文（从 SSO Token 解析出的不可变对象）。
- **AgentUserContext**: 默认实现，包含 UserId、TenantId、Roles、Claims、Audience
- 生命周期内不可变

### 2.5 SkillMetadata / McpMetadata
用于定义 Skill 和 MCP 组件向 Matrix 注册时的元数据标准。
- **SkillMetadata**: 包含 Name、Description、Version、JsonSchema、RequiredClaims、RequiredRoles、RequiresHumanApproval
- **McpMetadata**: 包含 ServerName、ServerUrl、ConnectionType、RequiredClaims

## 3. 异常与错误码体系

### 3.1 AgentErrorCode 枚举
统一定义的错误码，采用分段编码：
- **1001-1999**: Skill 相关错误（UnauthorizedSkill、SkillNotFound、SkillExecutionFailed、SkillTimeout 等）
- **2001-2999**: MCP 相关错误（ConnectionFailed、ToolNotFound、ToolExecutionFailed、ConnectionTimeout 等）
- **3001-3999**: RAG 相关错误（RetrievalFailed、IndexNotFound、PermissionDenied）
- **4001-4999**: LLM 相关错误（ProviderNotSupported、ConnectionFailed、Timeout、QuotaExceeded、InvalidResponse、ModelNotFound）
- **5001-5999**: 多租户相关错误（TenantMismatch、TenantNotFound、TenantDataIsolationViolation）
- **6001-6999**: 受众权限相关错误（AudiencePermissionDenied、AudienceMismatch）
- **7001-7999**: 人工审批相关错误（HumanApprovalRequired、HumanApprovalDenied、HumanApprovalTimeout）
- **8001-8999**: 请求相关错误（InvalidRequest、MissingRequiredField、InvalidIdempotencyKey）
- **9001-9999**: 内部错误（InternalError、PipelineExecutionFailed、ConfigurationError、DependencyUnavailable）

### 3.2 自定义异常类
- **AgentException**: 基类异常，包含 ErrorCode 和 Details
- **ToolExecutionException**: 工具执行失败异常，包含 ToolName 和 Arguments
- **HumanApprovalRequiredException**: 挂起等待人工审批异常，包含 ActionDescription 和 ApprovalToken
- **AudiencePermissionDeniedException**: 多方受众权限交集校验失败异常，包含 DeniedAudiences 和 RequiredPermission
- **TenantDataIsolationException**: 多租户数据隔离违规异常

## 4. 版本与兼容性机制
- **Schema 演进**: 所有新 DTO 字段默认为 Optional。废弃字段需标记 `[Obsolete]` 至少一个大版本周期
- **Idempotency 契约**: 对于具有副作用的写操作，调用方必须通过 HTTP Header 或 Request 实体透传 `Idempotency-Key`
- **契约版本号**: 所有对外 DTO、接口与错误码必须带有明确版本标识（如 `v1`, `v2`），并在发布说明中记录变更类型
- **向后兼容窗口**: 破坏性变更必须采用"双轨兼容"策略，在约定窗口内同时支持旧版本与新版本，禁止直接替换
