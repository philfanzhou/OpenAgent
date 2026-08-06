# Skill

Skill 是 Agent.Core 中模型可调用的执行能力，用于承载明确的动作操作。`SkillRegistry` 是已注册技能的唯一存储，`SkillCapabilitySource` 负责将当前 Agent 配置可用的技能转换为能力定义。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 技能发现 | 从 `SkillRegistry` 获取已注册的本地和动态技能 |
| 配置过滤 | 根据 `SkillsConfig.EnabledSkills` / `Instances` 过滤 |
| 权限过滤 | 基于用户上下文 ACL（AllowedUserIds / Groups / TenantIds / Roles）|
| 执行路由 | `SkillCapabilitySource` 直接调用 `SkillRegistry` |
| 动态注册 | 通过 `IToolRegistry.RegisterTool` 注册 |

## Source Layers
| Source | SkillSource |
|--------|-------------|
| 本地 Skill | `Local`（IToolRegistry + ISkill）|
| MCP 外部工具 | `Mcp` |
| Matrix 平台 | `Matrix` |

## Current Status
**Implemented** — 技能发现、配置/ACL 过滤与执行链路均由两层完成，无额外 Provider 转发层。

## Limits
- 无技能调用配额控制（`SkillQuotaExceeded` 错误码已定义但未使用）
- 无技能参数验证链路（`SkillValidationFailed` 错误码已定义但未使用）

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/SkillCapabilitySource.cs`, `Backend/src/OpenAgent.Core/Capabilities/Skill/SkillRegistry.cs`
- Contracts: `Backend/src/OpenAgent.Core/Abstract/IToolRegistry.cs`, `Backend/src/OpenAgent.Contracts/Skills/ISkill.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/SkillCapabilitySourceTests.cs`
