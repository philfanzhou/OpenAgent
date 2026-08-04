
## Feature


## 用户故事

作为执行内核，我希望模型可调用的执行能力被统一发现、过滤和执行，以便不同来源的能力在工具调用层面保持一致的语义。

## 概述

Skill 是 Agent.Core 中模型可调用的执行能力之一，用于承载明确的动作操作，而非知识检索。技能来源分为本地注册（ISkill/IToolRegistry）、MCP 外部工具和动态注册三类。

## 核心能力

- 按 AgentConfig.Skills 列表过滤可用技能
- 基于用户上下文的 ACL 权限过滤
- 统一收集为 SkillDescriptor 列表供引擎使用
- 按优先级路由执行：ToolRegistry → ISkillService → MCP

## 来源分层

| 来源 | SkillSource 枚举值 | 说明 |
|------|-------------------|------|
| 本地 Skill | `Local` | 运行时直接注册的工具能力（IToolRegistry）和本地 ISkill 实现 |
| MCP 外部工具 | `Mcp` | 通过 MCP 协议暴露的外部能力 |
| Matrix 平台 | `Matrix` | 运行时动态注入的外部平台能力 |

## 核心流程

1. **收集**：从 ToolRegistry、ISkillService、IMcpClient、动态注册四个渠道收集 SkillDescriptor
2. **配置过滤**：根据 SkillsConfig 过滤启用的 Skill
3. **权限过滤**：根据 IAgentUserContext 过滤可见 Skill
4. **暴露**：过滤后的 SkillDescriptor 转为 ToolDefinition 供引擎使用
5. **执行**：模型发起 Tool Call 后，由 SkillProvider 路由到对应执行器

## 当前状态

**已实现** — 技能发现、过滤与执行链路均已落地。

## 当前限制

- MCP 描述符收集使用同步 `.GetAwaiter().GetResult()`，在高并发场景下可能阻塞
- 无技能调用配额控制（SkillQuotaExceeded 错误码已定义但未使用）
- 无技能参数验证链路（SkillValidationFailed 错误码已定义但未使用）

## Architecture


## 收集流程

SkillProvider.GetSkillDescriptorsAsync 按以下顺序收集描述符：

1. **ToolRegistry 描述符**：遍历 IToolRegistry.GetTools()，每个 ToolDefinition 转为 SkillDescriptor（Source=Local）
2. **本地 Skill 描述符**：遍历 ISkillService.GetSkills()（运行时注册的 `_registeredSkills`），跳过已存在的同名项，转为 SkillDescriptor（Source=Local）
3. **MCP 描述符**：若 IMcpClient.IsConnected，调用 ListToolsAsync()，跳过已存在的同名项，转为 SkillDescriptor（Source=Mcp）
4. **动态描述符**：遍历运行时通过 RegisterSkill/RegisterMcpSkills 注册的描述符，跳过已存在的同名项

同名判断：Name 字段忽略大小写比较（StringComparison.OrdinalIgnoreCase）。

## 配置过滤

FilterByConfig 逻辑：

1. 若 SkillsConfig 为 null，不过滤，返回全部
2. 若 Instances 列表非空，取 Enabled=true 的实例，按 Name/Id 匹配描述符
3. 若 EnabledSkills 列表非空，按 Name 匹配描述符
4. 若配置存在但 Instances 和 EnabledSkills 均为空数组，返回空列表（不启用任何 Skill）

## 权限过滤

FilterByPermission 逻辑（IsAllowedForUser 静态方法）：

1. 若描述符的四个 ACL 列表均为空，允许所有用户访问
2. 若 userContext 为 null 且 ACL 列表非空，拒绝访问
3. 依次检查 AllowedUserIds、AllowedGroups、AllowedTenantIds、AllowedRoles，任一匹配即允许（OR 语义）

## 执行路由

SkillProvider.ExecuteAsync 按以下优先级路由：

1. 若 IToolRegistry.HasTool(name) 为 true → IToolRegistry.ExecuteToolAsync
2. 若 ISkillService 中存在同名 Skill → ISkillService.ExecuteSkillAsync
3. 否则 → IMcpClient.CallToolAsync

## 注册行为

- SkillService 是精简的注册容器：构造函数仅依赖 `ILogger<SkillService>`，不再注入 `IServiceProvider` / `IEnumerable<ISkill>`
- SkillService.RegisterSkill(ISkill) 检查名称重复（忽略大小写），重复时跳过并记录 Warning 日志
- SkillService.GetSkills 返回 `_registeredSkills`（不再拼接 DI 注入的 `_injectedSkills`）
- SkillProvider.RegisterSkill 同时注册到 ISkillService 和动态描述符列表
- SkillProvider.RegisterMcpSkills 为每个 MCP 工具创建描述符，Id 格式为 `{serverUrl}:{toolName}`
- 已移除的能力：`RegisterSkill<T>` 泛型注册、`ActivatorUtilities.CreateInstance`、执行时创建 DI scope 获取 scoped ISkill

## SkillService 执行细节

ExecuteSkillAsync 中：
- 直接调用 `skill.ExecuteAsync`，不再创建 DI scope 获取 scoped 服务
- 执行异常时捕获并返回 `"Error: {ex.Message}"` 字符串（不抛出）

## 错误处理

### 错误码

与 Skill 相关的 AgentErrorCode：

| 错误码 | 值 | 说明 |
|--------|-----|------|
| `UnauthorizedSkill` | 1001 | 用户无权访问该技能 |
| `SkillNotFound` | 1002 | 技能未找到 |
| `SkillExecutionFailed` | 1003 | 技能执行失败 |
| `SkillTimeout` | 1004 | 技能执行超时 |
| `SkillValidationFailed` | 1005 | 技能参数验证失败 |
| `SkillQuotaExceeded` | 1006 | 技能调用配额超限 |

### 异常类型

**ToolExecutionException** — 工具执行失败异常，继承自 AgentException。

| 属性 | 说明 |
|------|------|
| `ToolName` | 失败的工具名称 |
| `Arguments` | 调用参数 |
| `ErrorCode` | 固定为 SkillExecutionFailed (1003) |

**HumanApprovalRequiredException** — 需要人工审批异常。

| 属性 | 说明 |
|------|------|
| `ActionDescription` | 待审批动作描述 |
| `ApprovalToken` | 审批令牌 |
| `ErrorCode` | 固定为 HumanApprovalRequired (7001) |

### 运行时错误处理

- SkillService.ExecuteSkillAsync：技能未找到 → 抛出 `ArgumentException`；执行异常 → 捕获后记录 Error 日志，返回错误字符串（不抛出）
- SkillProvider.ExecuteAsync：按 ToolRegistry → SkillService → MCP 优先级路由，异常由对应层处理
- SkillService.RegisterSkill\<T\>：创建实例失败 → 捕获异常，记录 Error 日志，不抛出
- SkillService.RegisterSkill：名称重复 → 记录 Warning 日志，跳过注册

### 排障指南

| 现象 | 可能原因 | 排查方向 |
|------|---------|---------|
| Skill 未出现在工具集合 | 配置过滤 | 检查 SkillsConfig.EnabledSkills 和 Instances |
| Skill 未出现在工具集合 | 权限过滤 | 检查 SkillDescriptor 的 ACL 列表和用户上下文 |
| Skill 未出现在工具集合 | 注册缺失 | 检查 DI 注册和 RegisterSkill 调用 |
| Skill 执行失败 | 参数错误 | 检查 ParametersJsonSchema 和传入参数 |
| Skill 执行失败 | 实现异常 | 查看 Error 级别日志 |

## Data Models


## SkillDescriptor

技能描述符，用于工具集合的收集与过滤。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 技能标识，通常与 Name 相同；MCP 动态注册时为 `{serverUrl}:{toolName}` |
| `Name` | `string` | 技能名称（唯一） |
| `Description` | `string` | 技能描述 |
| `ParametersJsonSchema` | `string` | 参数 JSON Schema |
| `Source` | `SkillSource` | 来源类型 |
| `SourceId` | `string?` | 来源标识（如 MCP 服务器 URL） |
| `AllowedUserIds` | `List<string>` | 允许访问的用户 ID |
| `AllowedGroups` | `List<string>` | 允许访问的组 |
| `AllowedTenantIds` | `List<string>` | 允许访问的租户 ID |
| `AllowedRoles` | `List<string>` | 允许访问的角色 |

## SkillSource 枚举

| 值 | 说明 |
|----|------|
| `Local` | 本地注册的技能 |
| `Mcp` | MCP 外部工具 |
| `Matrix` | Matrix 平台能力 |

## SkillMetadata

技能元数据，用于声明式注册。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Name` | `string` | 技能名称（必填） |
| `Description` | `string` | 技能描述（必填） |
| `Version` | `string` | 版本号（必填） |
| `JsonSchema` | `string?` | 参数 Schema |
| `RequiredClaims` | `IReadOnlyList<string>?` | 所需声明 |
| `RequiredRoles` | `IReadOnlyList<string>?` | 所需角色 |
| `RequiresHumanApproval` | `bool` | 是否需要人工审批，默认 false |

## SkillContext

技能执行上下文（IAsyncSkill 使用）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `SkillName` | `string` | 技能名称 |
| `Arguments` | `Dictionary<string, object>` | 调用参数 |
| `UserContext` | `IAgentUserContext` | 用户上下文 |
| `TraceId` | `string?` | 追踪 ID |
| `TenantId` | `string?` | 租户 ID |
| `CancellationToken` | `CancellationToken` | 取消令牌 |

## SkillResult

技能执行结果（IAsyncSkill 使用）。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Success` | `bool` | 是否成功 |
| `Output` | `string?` | 输出内容 |
| `ErrorMessage` | `string?` | 错误信息 |
| `ErrorCode` | `AgentErrorCode?` | 错误码 |

## SkillsConfig

技能配置模型。

| 属性 | 类型 | 说明 |
|------|------|------|
| `EnabledSkills` | `List<string>` | 启用的技能名称列表 |
| `Instances` | `List<SkillInstanceConfig>` | 技能实例配置列表 |

## SkillInstanceConfig

技能实例配置。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 实例 ID |
| `Name` | `string` | 实例名称 |
| `Enabled` | `bool` | 是否启用 |
| `Description` | `string` | 描述 |
| `ParametersJsonSchema` | `string` | 参数 Schema |
| `Type` | `string?` | 类型 |
| `EndpointUrl` | `string?` | 远程端点 |
| `Version` | `string?` | 版本 |
| `Source` | `string` | 来源，默认 "Local" |
| `SourceId` | `string?` | 来源标识 |
| `AllowedUserIds` | `List<string>` | ACL |
| `AllowedGroups` | `List<string>` | ACL |
| `AllowedTenantIds` | `List<string>` | ACL |
| `AllowedRoles` | `List<string>` | ACL |

## API


## ISkillProvider

技能发现与执行的统一入口。

| 方法 | 说明 |
|------|------|
| `GetSkillDescriptorsAsync(agentId?, userContext?, overrideConfig?, ct)` | 收集并过滤当前可用的 SkillDescriptor 列表 |
| `ExecuteAsync(skillName, arguments, userContext?, ct)` | 按名称路由执行技能 |
| `RegisterSkill(ISkill, source, sourceId?)` | 动态注册技能 |
| `RegisterMcpSkills(serverUrl, tools)` | 批量注册 MCP 工具为技能 |

实现类：`SkillProvider`（internal）

## ISkillService

本地技能注册与执行管理。

| 方法 | 说明 |
|------|------|
| `RegisterSkill(ISkill)` | 直接注册技能实例（名称重复跳过） |
| `GetSkills()` | 获取所有运行时注册的 ISkill |
| `ExecuteSkillAsync(skillName, arguments, ct)` | 按名称执行本地技能（异常返回错误字符串） |

实现类：`SkillService`（internal）

## ISkill

单个技能的执行契约。

| 成员 | 说明 |
|------|------|
| `Name` | 技能唯一名称 |
| `Description` | 技能描述 |
| `ExecuteAsync(arguments, ct)` | 执行技能并返回字符串结果 |

## ISkillExecutor

工具执行器契约，RagSearchTool 实现此接口。

| 成员 | 说明 |
|------|------|
| `Name` | 工具名称 |
| `Description` | 工具描述 |
| `ParametersJsonSchema` | 参数 JSON Schema |
| `ExecuteAsync(toolName, arguments, userContext?, ct)` | 执行工具 |

## IAsyncSkill

带上下文的异步技能契约。

| 成员 | 说明 |
|------|------|
| `Name` | 技能名称 |
| `Description` | 技能描述 |
| `ExecuteAsync(SkillContext, ct)` | 在完整上下文中执行，返回 SkillResult |

## IToolRegistry

运行时工具注册表。

| 方法 | 说明 |
|------|------|
| `RegisterTool(ToolDefinition, executor)` | 注册工具及执行器 |
| `GetTools()` | 获取所有已注册的 ToolDefinition |
| `ExecuteToolAsync(toolName, arguments, ct)` | 执行已注册的工具 |
| `HasTool(toolName)` | 判断工具是否已注册 |

## 调用方使用模式

### 收集可用技能

```csharp
var descriptors = await skillProvider.GetSkillDescriptorsAsync(agentId, userContext, config.Skills, ct);
var toolDefinitions = descriptors.Select(d => new ToolDefinition
{
    Name = d.Name,
    Description = d.Description,
    ParametersJsonSchema = d.ParametersJsonSchema
}).ToList();
```

### 执行技能

```csharp
var result = await skillProvider.ExecuteAsync(skillName, arguments, userContext, ct);
```

### 动态注册技能

```csharp
skillProvider.RegisterSkill(new MySkill(), SkillSource.Local);
skillProvider.RegisterMcpSkills(serverUrl, mcpTools);
```

## Tests


## 测试策略

技能发现与执行的测试围绕收集、过滤、执行路由三个核心链路展开。

## 单元测试

### SkillService

| 测试场景 | 验证点 |
|----------|--------|
| RegisterSkill 注册新技能 | GetSkills 返回包含新注册的技能 |
| RegisterSkill 名称重复 | 跳过注册，记录 Warning 日志 |
| GetSkills 仅返回运行时注册 | 返回 `_registeredSkills`（不再拼接 DI 注入） |
| ExecuteSkillAsync 正常执行 | 返回技能执行结果（直接调用 skill.ExecuteAsync） |
| ExecuteSkillAsync 技能未找到 | 抛出 ArgumentException |
| ExecuteSkillAsync 执行异常 | 返回 "Error: ..." 字符串，不抛出 |

### SkillProvider 收集

| 测试场景 | 验证点 |
|----------|--------|
| ToolRegistry 描述符收集 | 所有 ToolDefinition 转为 SkillDescriptor（Source=Local） |
| 本地 Skill 描述符收集 | ISkill 转为 SkillDescriptor，跳过同名项 |
| MCP 描述符收集 | IsConnected 时收集 MCP 工具，跳过同名项 |
| 动态描述符收集 | 运行时注册的描述符被包含，跳过同名项 |
| MCP 未连接 | 跳过 MCP 描述符收集 |

### SkillProvider 过滤

| 测试场景 | 验证点 |
|----------|--------|
| SkillsConfig 为 null | 不过滤，返回全部 |
| Instances 非空 | 按 Enabled=true 的 Name/Id 匹配 |
| EnabledSkills 非空 | 按 Name 匹配 |
| 配置存在但列表为空 | 返回空列表 |
| ACL 全空 | 允许所有用户 |
| ACL 非空 + userContext 为 null | 拒绝访问 |
| ACL 匹配 | 任一维度匹配即允许 |

### SkillProvider 执行路由

| 测试场景 | 验证点 |
|----------|--------|
| ToolRegistry 有该工具 | 路由到 ToolRegistry.ExecuteToolAsync |
| ISkillService 有该技能 | 路由到 SkillService.ExecuteSkillAsync |
| 其他 | 路由到 McpClient.CallToolAsync |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整收集-过滤-执行链路 | 从注册到执行结果返回的完整流程 |
| 配置过滤与权限过滤组合 | 两个过滤维度正确叠加 |
| 动态注册后立即可用 | RegisterSkill 后 GetSkillDescriptorsAsync 包含新技能 |

## 验收口径

- [ ] 四个渠道的描述符均能正确收集
- [ ] 配置过滤和权限过滤行为符合预期
- [ ] 执行路由优先级正确：ToolRegistry > ISkillService > MCP
- [ ] 同名技能去重（忽略大小写）
- [ ] 注册失败不中断系统运行

## Conventions


## 命名约定

- Skill 名称应体现业务动作，而非实现细节
- 与 MCP 工具和系统保留工具区分清楚
- 长期稳定，不随实现细节频繁变化
- 名称比较忽略大小写（OrdinalIgnoreCase）

## 架构约定

- 机制留在 Core（ISkillProvider、ISkillService、IToolRegistry）
- 业务能力留在外部业务工程
- 示例 Skill 只用于演示和测试，不应被误认为 Core 内置业务能力

## 注册约定

- 同名 Skill 只注册一次，后到的重复注册会被跳过
- 优先级：ToolRegistry > ISkillService > MCP
- 动态注册的 Skill 需要同时注册到 ISkillService 和描述符列表

## 配置约定

- SkillsConfig 为 null 表示不过滤（全部可用）
- SkillsConfig 存在但 EnabledSkills 和 Instances 均为空 → 不启用任何 Skill
- Instance 的 Name 和 Id 均可用于匹配

## 权限约定

- 四个 ACL 列表（AllowedUserIds、AllowedGroups、AllowedTenantIds、AllowedRoles）均为空时，允许所有用户
- ACL 列表非空时，userContext 为 null 的请求将被拒绝
- 任一 ACL 维度匹配即允许（OR 语义）

## 可观测性约定

- Skill 注册重复：Warning 级别
- Skill 注册失败：Error 级别
- Skill 执行开始：Information 级别
- Skill 执行失败：Error 级别
- MCP 工具收集失败：Warning 级别

## 运行时要求

- 可被统一收集为工具描述
- 可在 Tool Calling 时被稳定执行
- 执行结果可回填到对话历史
- 出错时具备清晰日志和错误语义
