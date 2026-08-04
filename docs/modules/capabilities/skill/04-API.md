# API: 技能发现与执行

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
