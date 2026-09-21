# PR-9：AgentConfig 拆分

| 项 | 内容 |
|----|------|
| 状态 | 规划（2026-09 会话架构审查后续浪潮） |
| 基线 | main@9b694ab |
| 类型 | 契约层重构，持久化**零 schema 变更**（见 2.3） |
| 依赖 | 无硬依赖；建议在 PR-7（TurnContext）之后，减少同文件冲突 |

## 1. 动机

### 1.1 引用面实测（基线 main@9b694ab，比审查口头数字更细）

`Backend/src/OpenAgent.Contracts/Configuration/AgentConfig.cs`（227 行，同文件还定义了
`LlmConfig`/`LlmProviderProfile`/`McpConfig`/`RagConfig`/`SkillsConfig` 等 12 个类型）：

- 精确类型引用（`\bAgentConfig\b` 词边界、后随非字母，排除 `AgentConfigEntity` 等前缀命名族）：
  **32 个文件**（src 16 + tests 16），横跨 **6 个 src 项目**
  （Contracts/Core/Engine/Engine.Host/Infrastructure/Router——Router 命中仅为注释与关联类型）。
- 宽匹配（含 `AgentConfigEntity`/`AgentConfigAccessor`/`AgentConfigurationEntity` 等命名族）：
  **61 个文件**（src 41 + tests 20），另覆盖 EF 迁移 Designer 快照 4 份。
- 变更频率：该文件在 HEAD 历史上共 **18 个 commit** 触及；最近 120 个 commit 内 **6 次**
  （1e7f539、b576cee、7dfd32e、12ad6c8、c2b2a5b 等，均为技能/沙箱/MCP 能力追加）。

### 1.2 真正的债务：能力源吃整只大象

新配置项的完整路径（每段都已核对）：

```text
前端 types.ts AgentConfigEntity.config（:273-287 手写镜像）
  → ConfigurationController（Engine.Host，[Route("api/v1/admin")] :18）
  → ConfigurationService（Engine/Config）
  → AgentConfigRepository（Infrastructure：按关注点分列存取）
  → AgentConfig（Contracts 大杂烩）
  → AgentRuntimeProfile（Contracts/Configuration/AgentRuntimeProfile.cs:9 唯一持有点）
  → AgentFactory / CapabilityToolFactory（:19-32 把整只 AgentConfig 递给每个 source）
  → 各 ICapabilitySource
```

核心接口是 `ICapabilitySource.DiscoverAsync(agentId, AgentConfig config, ...)`
（`Backend/src/OpenAgent.Core/Capabilities/ICapabilitySource.cs:6-13`），但实测各消费方
真正需要的只是一个小节：

| 消费方 | 实际使用的成员 | 核对位置 |
|--------|----------------|----------|
| `CodeCapabilitySource` | `config.CodeExecution`（+自身 options） | `CodeCapabilitySource.cs:25` |
| `RagCapabilitySource` | `config.Rag` | `RagCapabilitySource.cs:38-49` |
| `AgentSkillsProviderFactory` | `config.Skills`、`config.CodeExecution`（技能脚本开关 :145） | `AgentSkillsProviderFactory.cs:48,54,145,167-184` |
| `UserProfileCapabilitySource` | **不用 config**（参数形同虚设） | `UserProfileCapabilitySource.cs:19` |
| `FileAssetCapabilitySource` | **不用 config** | `FileAssetCapabilitySource.cs:21` |
| `McpToolFactory` | **已窄化**为 `McpConfig`（`AgentFactory.cs:89` 传 `profile.Config.Mcp`） | `McpToolFactory.cs:23-27` |
| `AgentChatClientFactory` | **已窄化**为 `ContextPolicy`（`AgentFactory.cs:103-106`） | `AgentChatClientFactory.cs:51,63` |
| `AgentRuntimeResolver` | `MaxTurns`（:99）、`Skills` 租户校验（:111-112）、整体透传 | `AgentRuntimeResolver.cs:36-69,85-121` |

即：**能窄化的已经窄化了两个（Mcp、ContextPolicy），剩下的债务集中在
`ICapabilitySource` 这个"整只传入"的接口**。任何给 Rag 加字段的人都要面对一个被
6 个项目引用、月均 1-2 次变动的类型。

### 1.3 精确引用分类清单（src 16 文件）

| 类别 | 文件 | 说明 |
|------|------|------|
| 契约定义 | `Contracts/Configuration/AgentConfig.cs` | 类型本体（含 12 个伴生类型，应顺势拆文件） |
| 契约投影 | `Contracts/Configuration/AgentRuntimeProfile.cs` | `Config` 属性持有（ADR-0003 的对外只读投影） |
| 读取接口 | `Contracts/Configuration/IAgentConfigProvider.cs` | 返回 `AgentConfig?` |
| 持久化接口 | `Contracts/Configuration/IAgentConfigRepository.cs` | 返回 `AgentConfigEntity` |
| API DTO | `Contracts/Models/AgentConfigEntity.cs:22` | `Config` 属性（管理面出入参） |
| 平台 API | `Contracts/Services/IAgentMatrixApi.cs:14` | Matrix 服务预留 |
| 能力源（5） | `ICapabilitySource.cs`、`CapabilityToolFactory.cs`、`Code/Rag/UserProfile/FileAsset` 各 source | **窄化主战场** |
| 能力源（2） | `AgentSkillsProviderFactory.cs`、（Skill） | 跨 Skills+CodeExecution 两节 |
| 运行时 | `AgentRuntimeResolver.cs` | 校验 + 组装 profile |
| 运行时 | `AgentChatClientFactory.cs` | **仅注释引用**（:44），无实依赖 |
| 目录 | `Core/Abstract/ISkillCatalog.cs:7` | **仅注释引用** |
| 持久化实现 | `Infrastructure/Configuration/AgentConfigRepository.cs` | 分列拆装（见 2.3） |

tests 16 文件（Core.Tests 11：Capabilities 7 + Runtime 4；Engine.Tests 3：
Config 2 + Hosting 1；Infrastructure.Tests 1；Contracts.Tests 1），与上表类别同构。

## 2. 方案设计

### 2.1 拆分原则：不拆对象，先拆依赖

审查建议按 `ModelBindingConfig`/`ToolPolicyConfig`/`ContextPolicyConfig` 拆小节。对照现状
（成员本就分节：`Mcp`/`Rag`/`Skills`/`CodeExecution`/`ContextPolicy`/`Instructions`/`MaxTurns`），
真正要拆的不是属性（它们已经是小节），而是**消费接口的参数形状**。分两步：

**第一步（本 PR 主体）：能力源接口窄化。**

```csharp
// Backend/src/OpenAgent.Core/Capabilities/ICapabilitySource.cs（改造后）
internal interface ICapabilitySource
{
    Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentRuntimeProfile profile,   // 或 CapabilityContext：见下方取舍
        IAgentUserContext user,
        CancellationToken cancellationToken);
}
```

推荐引入窄参对象而非继续传分节：

```csharp
/// 能力发现所需的只读视图：只暴露能力相关小节，按需扩节。
internal sealed record CapabilityContext(
    string AgentId,
    string TenantId,
    McpConfig Mcp,                 // 现状已由 McpToolFactory 单独收
    RagConfig Rag,
    SkillsConfig Skills,
    CodeExecutionConfig CodeExecution)
{
    public static CapabilityContext From(AgentRuntimeProfile profile) => /* ... */;
}
```

- `UserProfileCapabilitySource`/`FileAssetCapabilitySource`：签名去掉 config 参数
  （它们不用）。
- `AgentSkillsProviderFactory`：跨 `Skills` + `CodeExecution` 两节——`CapabilityContext`
  一次给齐，无需特判。
- `AgentRuntimeResolver` 校验 `MaxTurns`/技能租户：保持读 `AgentConfig`（它在
  Contracts 侧本来就是配置门面，见 ADR-0003）。

**第二步（可选后续）：`AgentConfig` 文件拆分。** 把 `LlmConfig`/`LlmProviderProfile`/
`ApiFormat`/`ModelModality` 移出 `AgentConfig.cs`（它们是模型档案不是 agent 配置），
`McpConfig`/`RagConfig`/`SkillsConfig`/`CodeExecutionConfig` 各自成文件。纯文件移动，
无语义变化。

**不做的**：不把 `AgentConfig` 拆成多个顶层配置对象再组合（如独立的
`ToolPolicyConfig` 根类型）——持久化与 API 形态已经是"一个 agent 一份组合配置"，
再拆一层根类型只移动复杂度。

### 2.2 AgentRuntimeProfile 保持唯一对外投影

遵循 `docs/decisions/0003-Agent-Runtime-Profile-Resolution.md`：`AgentRuntimeProfile`
（`AgentRuntimeProfile.cs:6-11`）继续持有完整 `Config`，`AgentExecutor`/`AgentFactory`
不变。窄化只发生在 Core 内部的能力发现链上（`CapabilityToolFactory.CreateAsync`
:19-32 改为构造 `CapabilityContext` 后分发）。这样 Engine/Router/Infrastructure 的
41 个宽匹配引用点绝大多数零改动。

### 2.3 持久化形态：维持分列 jsonb，零 migration

实测 `AgentConfigurationEntity`（`Backend/src/OpenAgent.Infrastructure/Entities/AgentConfigurationEntity.cs:5-21`）
**已经**按关注点分列：`Instructions`/`MaxTurns` 平铺列 + `ContextPolicyJson`/`McpJson`/
`RagJson`/`SkillsJson`/`CodeExecutionJson` 五个 jsonb 列；`AgentConfigRepository`
写侧 :105-112 分列序列化、读侧 :143-153 重组装。**"jsonb 子文档 vs 平铺列"的选择已经在
现状里做完了**，本 PR 不动 schema、不产生 migration，仅评估性结论：

- 平铺列（Instructions/MaxTurns）：可索引、可约束，保持；
- jsonb 子文档（各能力节）：随节内演进免 migration，保持；
- 若未来某节需要跨 agent 查询（如"哪些 agent 启用了 CodeExecution"），再为该节提升
  生成列，不属本 PR。

## 3. 影响面清单（文件级）

| 文件 | 变化 |
|------|------|
| `Backend/src/OpenAgent.Core/Capabilities/ICapabilitySource.cs` | 接口参数窄化为 `CapabilityContext` |
| `Backend/src/OpenAgent.Core/Capabilities/CapabilityToolFactory.cs` | :19-32 构造 context 分发 |
| `Backend/src/OpenAgent.Core/Capabilities/Code/CodeCapabilitySource.cs` | 改收 `CodeExecutionConfig` |
| `Backend/src/OpenAgent.Core/Capabilities/Rag/RagCapabilitySource.cs` | 改收 `RagConfig` |
| `Backend/src/OpenAgent.Core/Capabilities/UserProfile/UserProfileCapabilitySource.cs` | 去掉 config 参数 |
| `Backend/src/OpenAgent.Core/Files/FileAssetCapabilitySource.cs` | 去掉 config 参数 |
| `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs` | 改收 context（Skills+CodeExecution） |
| `Backend/src/OpenAgent.Core/Runtime/Agent/AgentFactory.cs` | :78-82 调用点适配 |
| `Backend/src/OpenAgent.Contracts/Configuration/AgentConfig.cs` | （子 PR-c）类型分文件搬迁 |
| `Backend/tests/OpenAgent.Core.Tests/Capabilities/*Tests.cs`（7 个） | source 测试装配适配 |
| `Backend/tests/OpenAgent.Engine.Tests/**`、`Infrastructure.Tests/**` | 若仅经 profile 驱动则零改动，逐个走查 |

Router/Engine.Host/Infrastructure 主链零改动（它们消费 `AgentConfigEntity`/`AgentConfig`，
不触 `ICapabilitySource`）。

## 4. 分阶段实施步骤（3 个子 PR）

1. **PR-9a：`CapabilityContext` 引入 + 能力源窄化**。新增窄参对象；
   `ICapabilitySource` 签名替换；6 个 source + `CapabilityToolFactory` 适配；
   同步能力测试文件。行为零变化，diff 集中在 Core。
2. **PR-9b：无依赖参数清理**。删除 `UserProfile/FileAsset` source 的死 config 参数、
   `AgentChatClientFactory.cs:44` 与 `ISkillCatalog.cs:7` 的陈旧注释引用；
   补 ArchUnitNet 风格断言（若 `OpenAgent.Architecture.Tests` 已有依赖方向测试则加
   "能力源不得引用 AgentConfig 根类型"规则）。
3. **PR-9c：`AgentConfig.cs` 文件拆分**。`LlmConfig`→`LlmConfig.cs`、
   `LlmProviderProfile`→`LlmProviderProfile.cs`、`Mcp/Rag/Skills/CodeExecution` 各节
   独立文件；using 修正。git mv 语义保证 blame 连续。

## 5. 风险与回滚

| 风险 | 缓解 |
|------|------|
| 能力源后续需要新小节（如 Skills 要读 `Instructions`）时又回到"整只传入" | `CapabilityContext` 加节是显式、review 可见的改动；Architecture.Tests 规则阻止绕过 |
| `AgentSkillsProviderFactory` 跨节需求演化成 context 膨胀 | 节上限锚定 `AgentConfig` 既有能力节；新增横切信息优先走 PR-7 的 TurnContext（身份类）而非配置类 |
| 测试装配面广（16 个测试文件精确引用，宽匹配 20 个） | 9a 与 9b 分开，各自 revert 成本低 |
| 9c 文件拆分引发 merge 冲突（该文件月均 1-2 次变动） | 纯移动不改内容；选在能力 PR 空窗期合入 |
| API/持久化形态误改 | 明确零 schema/零 API 契约变化；`ContractSerializationTests` 与 EF 仓库测试作为门禁 |

回滚：三个子 PR 均为代码级改动，无数据迁移，单独 revert 即可。

## 6. 验收标准

1. `OpenAgent.Core/Capabilities` 与 `OpenAgent.Core/Files` 下（`ICapabilitySource`
   实现方）不再出现 `AgentConfig` 类型引用（注释除外），改由 `CapabilityContext`
   或具体节类型入参。
2. `UserProfileCapabilitySource`/`FileAssetCapabilitySource` 签名无 config 参数。
3. 精确引用计数从 32 降至 ≤ 20（src 侧从 16 降至 ≤ 10，剩余为 Contracts 定义/投影/
   接口、Resolver、Repository 与其测试）；宽匹配 src 计数不上升。
4. 给 `RagConfig` 加一个字段的演练：只改 `RagConfig.cs`、`RagCapabilitySource`、
   仓库/前端镜像四处，`CapabilityToolFactory` 与其他 source 零改动（PR 描述留证）。
5. 全量 `dotnet test Backend/OpenAgent.sln` 通过；EF 无新 migration 生成
   （`dotnet ef migrations has-pending-model-changes` 为 false 或等价验证）。
6. ADR-0003 的调用链（Endpoint → AgentExecutor → Resolver → Factory → Scope）不变，
   `AgentRuntimeProfile` 仍是唯一对外只读投影。
