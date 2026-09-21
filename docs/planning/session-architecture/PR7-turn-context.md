# PR-7：统一轮次上下文 TurnContext

| 项 | 内容 |
|----|------|
| 状态 | 规划（2026-09 会话架构审查后续浪潮） |
| 基线 | main@9b694ab |
| 类型 | 重构（行为保持），Core 内部 API 变化 |
| 依赖 | 无硬依赖；建议在 PR-5/PR-6（会话存储与历史拆分）合并后实施，避免冲突 |

## 1. 动机

### 1.1 一次请求拼装三套并行 scope

`AgentFactory.CreateAsync`（`Backend/src/OpenAgent.Core/Runtime/Agent/AgentFactory.cs:44-77`）在一次
agent 轮次开始时，手工构造三份语义高度重叠的上下文对象：

1. **LlmInteractionCapture**（:54-62）：`TenantId`/`UserId`/`ConversationId`/`TraceId`/`AgentId` +
   `Source`，用于 LLM 交互日志脱敏记录（`LlmInteractionCapture.cs:9-17`）；:63 再用 `with` 克隆出
   压缩专用的一份（仅 `Source` 不同）。
2. **FileAssetScope**（:65-70）：`TenantId`/`UserId`/`ConversationId`，通过
   `_files.Set(...)` 写进 **DI Scoped** 的可变单例 `FileAssetExecutionContext`
   （`FileAssetExecutionContext.cs:13-16`；注册见 `FileAssetServiceExtensions.cs:29`
   `services.AddScoped<FileAssetExecutionContext>()`）。
3. **ConversationContext**（经由 `_conversations.Create(...)` :71-77）：在
   `ConversationHistoryFactory.Create`（`ConversationHistoryFactory.cs:64-70`）与
   `EnsureConversationAsync`（:85-91）内再拼一次六元组
   `(ConversationId, TenantId, UserId, AgentId, TraceId, Type)`（`ConversationContext.cs:6-17`）。

同一个 `TenantId ?? string.Empty` 归一化在 :56 与 :67 出现两次；`TraceId` 兜底逻辑
（`string.IsNullOrWhiteSpace ? Guid.NewGuid().ToString("N")`）在 `AgentFactory.cs:51-53` 与
`AgentExecutor.ResolveTraceId`（`AgentExecutor.cs:268-269`，调用点 :41 与 :107）**重复实现**。
由于 `AgentExecutor` 总是先解析并经 `CopyWithResolvedValues`（:51-55, :271-287）回填请求，
`AgentFactory` 内的兜底分支在主链路上是死代码——这是双份真相的典型症状。

### 1.2 新横切字段的成本

任何新的轮次级横切字段（租户配额标记、审计开关、采样标记、客户端类型等）今天需要：

- 改 `LlmInteractionCapture`（Core，internal record）；
- 改 `FileAssetScope`（Contracts）与 `FileAssetExecutionContext.Set` 调用点；
- 改 `ConversationContext` / `ConversationHistoryFactory` 两个构造点；
- 若要落库还需穿 `ConversationSessionStore.SaveAsync`（`ConversationSessionStore.cs:130`）。

即"一个字段穿一层工厂"。`ClientType`/`IdempotencyKey` 已被 ADR-0003 后续工作
（`docs/decisions/0003-Agent-Runtime-Profile-Resolution.md:51`）点名为待拆出的"内部调用元数据"，
本 PR 是它的载体。

## 2. 方案设计

### 2.1 核心类型：TurnContext

放在 `OpenAgent.Contracts`（与 `FileAssetScope` 同层，供 Infrastructure/Router 复用）：

```csharp
// Backend/src/OpenAgent.Contracts/Runtime/TurnContext.cs
public sealed record TurnContext
{
    public required string TenantId { get; init; }        // 已归一化，绝不为 null
    public required string UserId { get; init; }
    public string? ConversationId { get; init; }
    public required string TraceId { get; init; }          // 已解析，绝不为空
    public string? AgentId { get; init; }
    public ConversationType? ConversationType { get; init; }

    // —— 派生投影：取代各处手工拼装 ——
    public FileAssetScope ToFileAssetScope() => new()
    {
        TenantId = TenantId, UserId = UserId, ConversationId = ConversationId
    };

    public LlmInteractionCapture ToCapture(LlmInteractionSource source) => new()
    {
        TenantId = TenantId, UserId = UserId, ConversationId = ConversationId,
        TraceId = TraceId, AgentId = AgentId, Source = source
    };

    // Core 侧 internal 扩展：ToConversationContext()
}
```

约束：

- **唯一构造点**在 `AgentExecutor`：解析 `agentId`（:42-45/:108-111）、`traceId`（:41/:107）、
  `conversationId`（:296-308）之后、调用 `CreateAsync` 之前构造一次；`AgentFactory.cs:51-53`
  的兜底与 `:56/:67` 的 `?? string.Empty` 随之删除。
- `AgentFactory.CreateAsync` 签名收敛为
  `CreateAsync(AgentRuntimeProfile profile, TurnContext turn, IReadOnlyList<FileAsset> files, ...)`，
  `request`/`user` 中已被 TurnContext 覆盖的字段不再单独传。
- `ConversationHistoryFactory.Create`/`EnsureConversationAsync` 接收 `TurnContext`，内部调用
  `turn.ToConversationContext()`；`PlatformChatHistoryContext`（`PlatformChatHistoryContext.cs:7`）
  保持不变，`PlatformChatHistoryFactory`（`PlatformChatHistoryFactory.cs:5-10`）零改动。

### 2.2 Ambient 传递的取舍（评估结论）

审查建议"以 AsyncLocal Ambient 取代逐层穿参"。核对现状后的结论：

- **主链路不需要 ambient**：Executor → Factory → History/ToolFactory 的深度只有两层，
  显式参数成本低于隐式上下文，且可测试性更好。
- **需要 ambient 的是工具回调深处**：`FileAssetExecutionContext` 今天靠 Scoped 生命周期 +
  `Set()` 可变状态把 scope 送进 `CodeCapabilitySource`（`CodeCapabilitySource.cs:16`）、
  `SkillScriptRunner`（`SkillScriptRunner.cs:25`）、`FileAssetCapabilitySource`
  （`FileAssetCapabilitySource.cs:15`）、`PlatformChatHistory`（`PlatformChatHistory.cs:28,53`）。
  这本质已是"Scoped 级 ambient"，问题在于它只承载 Files 一个维度。
- **推荐形态**：`TurnContext` 显式传参为主；`FileAssetExecutionContext` 改为持有
  `TurnContext`（`Set(TurnContext)`），成为 scoped ambient 的唯一入口。真正的
  `AsyncLocal<TurnContext>` **不引入**：`FunctionInvokingChatClient`
  （`AgentFactory.cs:122-134`，`AllowConcurrentInvocation = false`）今天串行，但 MAF
  升级后并行工具调用会让 AsyncLocal 的可见性语义变成隐式契约，收益不抵风险。
  若未来 Runner/技能脚本进程外执行需要透传，再评估显式序列化字段（作为 TurnContext 的新成员，
  恰好只改一处）。

### 2.3 迁移策略：先并行走查，再切换

- 阶段 1 不改行为：`AgentExecutor` 构造 `TurnContext` 后，仅用于 **contract 断言**
  （Debug 检查三套 scope 与 TurnContext 字段一致），原拼装路径保留。
- 阶段 2 逐点替换派生：`turnCapture` → `turn.ToCapture(AgentTurn)`、
  `_files.Set(...)` → `_files.Set(turn)`、`ConversationContext` → 派生。
- 阶段 3 删除重复解析与 `?? string.Empty` 归一化，收紧 `AgentFactory` 签名。

## 3. 影响面清单（文件级）

| 文件 | 变化 |
|------|------|
| `Backend/src/OpenAgent.Contracts/Runtime/TurnContext.cs` | 新增 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs` | 唯一构造点；删除 :41/:107/:268-269 重复解析（收敛为私有 `CreateTurn`） |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentFactory.cs` | `CreateAsync`/`EnsureConversationAsync` 签名改 TurnContext；删除 :51-70 的三段拼装 |
| `Backend/src/OpenAgent.Core/Runtime/Agent/LlmInteractionCapture.cs` | 保留；新增（或移到 partial）`TurnContext.ToCapture` |
| `Backend/src/OpenAgent.Core/Files/FileAssetExecutionContext.cs` | `Set(FileAssetScope)` → `Set(TurnContext)`；`Scope` 属性改为按需投影 |
| `Backend/src/OpenAgent.Core/Files/FileAssetServiceExtensions.cs` | 注册不变（仍 Scoped） |
| `Backend/src/OpenAgent.Core/Conversation/ConversationHistoryFactory.cs` | `Create`/`EnsureConversationAsync`/`CreateCompaction` 入参收敛 |
| `Backend/src/OpenAgent.Core/Conversation/ConversationContext.cs` | 构造仅剩派生处一处；`IsValid` 不变 |
| `Backend/src/OpenAgent.Core/Conversation/PlatformChatHistory.cs` | `_conversation`/`_fileExecution` 使用处适配（:193 等） |
| `Backend/src/OpenAgent.Core/Capabilities/**`（Code/Rag/Skill 源） | 经 `FileAssetExecutionContext` 间接适配，签名不变 |
| `Backend/tests/OpenAgent.Core.Tests/**` | `FileAssetCapabilitySourceTests.cs:516-541`、`CodeCapabilityTests.cs:41,264-272`、`SkillScriptRunnerTests.cs:268-274` 等直接 `new FileAssetExecutionContext()` + `Set(new FileAssetScope{...})` 的装配点改传 TurnContext |
| `Backend/tests/OpenAgent.Core.Tests/Runtime/AgentExecutor*Tests.cs` | 走查断言测试 |

Engine.Host / Router / Infrastructure 零改动（TurnContext 不出 Core 边界，除 Contracts 定义）。

## 4. 分阶段实施步骤

1. **PR-7a（走查）**：新增 `TurnContext` + `AgentExecutor` 构造 + 三处 `Debug.Assert` 等价
   测试（`turn.TenantId == capture.TenantId` 等）；不改任何现有签名。
2. **PR-7b（切换）**：`AgentFactory`/`ConversationHistoryFactory`/`FileAssetExecutionContext`
   改为消费 TurnContext；测试装配点同步；删除重复 traceId/tenant 归一化。
3. **PR-7c（收口，可选）**：`ClientType`、`IdempotencyKey` 从 `AgentRequest` 迁入 TurnContext
   （兑现 ADR-0003 后续工作第 2 条），`ExternalContext` 一并评估。

每个子 PR 独立可合并、可回滚（revert 单个 commit 即可，无数据/契约迁移）。

## 5. 风险与回滚

| 风险 | 缓解 |
|------|------|
| Scoped `FileAssetExecutionContext` 在同一 scope 并发多轮时被 `Set` 覆盖（现状已存在，切换后语义不变但更显眼） | 保持一轮一 scope 的现状；`Set` 加"重复设置同值幂等、异值抛错"防御，把隐患显式化 |
| async flow：若误引入 AsyncLocal，`FunctionInvokingChatClient` 工具回调的 ExecutionContext 拷贝语义造成"读到旧值" | 方案明确不使用 AsyncLocal（见 2.2）；代码评审检查点 |
| 测试主机装配：多个测试直接 `new FileAssetExecutionContext().Set(new FileAssetScope{...})` | PR-7b 同步改造；提供 `TurnContextTestFactory` 帮助类降低样板 |
| `EnsureConversationAsync` 在 `AgentExecutor` 中先于文件解析调用（`AgentExecutor.cs:56-63`），此时 conversationId 可能刚生成 | TurnContext 在两个调用点之前构造即可覆盖；走查阶段用测试证明 |

回滚：纯代码重构，无 DB/契约迁移；revert 对应子 PR 即可。

## 6. 验收标准

1. `grep -rn "Guid.NewGuid().ToString(\"N\")" Backend/src/OpenAgent.Core/Runtime` 只剩
   TurnContext 构造点一处 traceId 兜底（`AgentExecutor` 内）。
2. `AgentFactory.CreateAsync` 内不再出现 `user.TenantId ?? string.Empty` 与裸
   `new FileAssetScope`/`new ConversationContext` 拼装（全部经 TurnContext 派生）。
3. 新增横切字段的模拟演练：在 TurnContext 加一个 `IsAudited` 字段，从 Executor 到
   `LlmInteractionCapture` 落库路径 **零额外穿参**（记录在 PR 描述作为证据）。
4. 全量 `dotnet test Backend/OpenAgent.sln` 通过；行为等价由既有
   `AgentExecutorUsageTests`/`ConversationSessionStoreTests` 保证，无快照变化。
5. LLM 交互日志、消息 TraceId 盖章（`ConversationSessionStore.cs:142-146`）行为不变，
   `d449647` 引入的对齐语义有回归测试覆盖。
