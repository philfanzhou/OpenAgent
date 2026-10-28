# ADR-0003：集中解析 Agent 运行配置

## 状态

已决策，第一版已实现。

## 背景

原来的 `AgentExecutor` 同时负责读取原始配置、解析 LLM Profile、执行 Agent/Model 权限检查和创建 Agent。`AgentRequest` 还携带了属于 Agent 配置的 `ContextPolicy` 与未被执行链消费的 `EnabledSkills`。

此外，`AgentLease` 只承担请求级资源释放和流式部分响应回写，名称容易让人误以为存在 Agent 租赁或复用机制。

## 决策

新增 `IAgentRuntimeResolver` 作为运行前的配置门面。它接收 AgentId 和用户身份，返回 `AgentRuntimeProfile`，负责协调：

- 原始 Agent 配置读取；
- LLM Profile 展开；
- Agent/Model 权限检查；
- 运行前的基础有效性校验。

第一版实现 `AgentRuntimeResolver` 仍组合 `IAgentConfigProvider` 与 `AgentAuthorizationGate`。后续可以把该门面替换为 Agent.Matrix 客户端，而不改变 `AgentExecutor` 的调用合同。

`ContextPolicy` 移入 `AgentConfig`，由 Agent Profile 决定；`EnabledSkills` 从 `AgentRequest` 移除，Skill 是否启用由 Profile 和能力授权决定。工具实际调用时继续执行二次权限检查，以防止能力发现后的权限变化造成 TOCTOU。

`AgentLease` 更名为 `AgentExecutionScope`。它明确持有 `AIAgent` 与本次请求的 `PlatformChatHistory`，仍负责取消状态写回和会话锁释放，但不引入新的业务执行层。

## 当前调用链

```text
Endpoint
  → AgentExecutor
      → IAgentRuntimeResolver
          → AgentRuntimeProfile
      → AgentFactory
          → AgentExecutionScope
              → AIAgent
```

## 影响

- `AgentExecutor` 不再依赖原始配置提供者和权限门闩的具体组合；
- Agent 配置解析和运行前校验有单一入口；
- 会话压缩策略不再由单次请求覆盖；
- `AgentRequest` 的配置职责减少；
- 仍保留能力调用时的权限复核和平台会话生命周期管理。

## 后续工作

- 将 `AgentRuntimeResolver` 的底层读取替换为独立 Agent.Matrix 服务客户端；
- 将 TraceId、ClientType、幂等键和外部上下文从 `AgentRequest` 拆到内部调用元数据；
- 明确定义会话创建时的 Profile/ContextPolicy 版本固定策略。

## 关键实现

- `Backend/src/OpenAgent.Contracts/Configuration/IAgentConfigProvider.cs`
- `Backend/src/OpenAgent.Core/Runtime/Agent/AgentRuntimeResolver.cs`
- `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutionScope.cs`
- `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs`
