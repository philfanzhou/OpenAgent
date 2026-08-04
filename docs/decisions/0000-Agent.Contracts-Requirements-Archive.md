# Agent.Contracts 规范说明 (Contracts & Abstractions Specification)

## 1. 模块定位
`Agent.Contracts` 是一个**纯净的类库 (Class Library / Assembly)**，它不是一个可独立运行的系统模块。
它的核心作用是作为整个 Agent 矩阵架构（以及外部独立业务线）的**全局标准契约层**。

**设计红线**：
1. 本工程**绝对不允许**包含任何具体的业务实现代码。
2. 本工程**绝对不允许**引用任何重量级的第三方框架（如 Entity Framework Core, Semantic Kernel, Redis SDK）。
3. 它必须保持极度的轻量级（仅依赖于 .NET 基础库，如 `System.Text.Json` 或 `Microsoft.Extensions.DependencyInjection.Abstractions`）。

## 2. 核心职责拆解

### 2.1 外部集成契约 (External Integration Interfaces)
这是外部独立业务系统（如 ERP、HR、OA）为了接入 `Agent.Matrix` 技能市场所必须依赖和实现的接口。
*   `ISkill` / `IAsyncSkill`: 定义标准技能的执行方法 `ExecuteAsync(SkillContext context)`。
*   `IMcpClient`: 定义 MCP 协议的标准调用接口。

### 2.2 内部流转模型 (Internal Data Transfer Objects - DTOs)
跨越 Router -> Core -> Workflow 边界传递的标准数据结构。
*   `AgentRequest`: 统一封装用户的 Query、TraceId、终端类型。
*   `AgentResponse`: 统一封装回答文本、引用的来源（Citations）、工具调用日志等，支持流式和非流式结构。
*   `IAgentUserContext`: 标准化的用户身份和权限上下文（从 SSO Token 解析出的不可变对象）。
*   `SkillMetadata` / `McpMetadata`: 用于定义 Skill 和 MCP 组件向 Matrix 注册时的元数据标准。

### 2.3 状态与异常契约 (Enums & Exceptions)
*   定义全局统一的错误码枚举（如 `AgentErrorCode.UnauthorizedSkill`）。
*   定义特定于 Agent 的自定义异常（如 `ToolExecutionException`, `HumanApprovalRequiredException`, `AudiencePermissionDeniedException`），供 Hosting 层统一捕获和处理。

### 2.4 版本与兼容策略 (Versioning & Backward Compatibility)
*   **契约版本号**: 所有对外 DTO、接口与错误码必须带有明确版本标识（如 `v1`, `v2`），并在发布说明中记录变更类型（兼容/破坏性）。
*   **向后兼容窗口**: 破坏性变更必须采用“双轨兼容”策略，在约定窗口内同时支持旧版本与新版本，禁止直接替换。
*   **Schema 演进约束**: 新增字段应默认可选且带默认值；删除或重命名字段必须先经历弃用阶段（Deprecated）。

### 2.5 幂等与重放防护契约 (Idempotency Contract)
*   **幂等键标准**: 写操作相关请求 DTO 必须支持 `IdempotencyKey` 字段或等效 Header 映射字段。
*   **结果重放语义**: 相同 `IdempotencyKey` + 相同业务参数的重复请求必须返回同一业务结果；参数不一致时返回冲突错误。

## 3. 价值与作用
通过将所有接口和 DTO 抽离到 `Agent.Contracts`，我们彻底解决了**循环依赖**问题，并为企业内其他研发团队提供了一个极低成本的 SDK 包（如 `OpenAgent.Contracts.nupkg`），使得他们可以轻松开发符合我们平台规范的 Skill。
