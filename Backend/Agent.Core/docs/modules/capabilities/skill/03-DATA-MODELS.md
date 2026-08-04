# Data Models: 技能发现与执行

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
