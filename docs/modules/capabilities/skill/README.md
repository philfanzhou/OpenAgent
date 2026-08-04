# Skill

Skill 是 Agent.Core 中模型可调用的执行能力，用于承载明确的动作操作。技能来源分为本地注册、MCP 外部工具和动态注册三类。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 技能发现 | 从 ToolRegistry / ISkillService / IMcpClient / 动态注册四渠道收集 |
| 配置过滤 | 根据 `SkillsConfig.EnabledSkills` / `Instances` 过滤 |
| 权限过滤 | 基于用户上下文 ACL（AllowedUserIds / Groups / TenantIds / Roles）|
| 执行路由 | ToolRegistry → ISkillService → MCP 优先级路由 |
| 动态注册 | `RegisterSkill` / `RegisterMcpSkills` 运行时注册 |

## Source Layers
| Source | SkillSource |
|--------|-------------|
| 本地 Skill | `Local`（IToolRegistry + ISkill）|
| MCP 外部工具 | `Mcp` |
| Matrix 平台 | `Matrix` |

## Current Status
**Implemented** — 技能发现、过滤与执行链路均已落地。

## Limits
- MCP 描述符收集使用同步 `.GetAwaiter().GetResult()`，高并发场景可能阻塞
- 无技能调用配额控制（`SkillQuotaExceeded` 错误码已定义但未使用）
- 无技能参数验证链路（`SkillValidationFailed` 错误码已定义但未使用）

## Source
- Core: `src/Core/Capabilities/Skill/SkillProvider.cs`, `SkillService.cs`
- Contracts: `Agent.Contracts/`（ISkill, ISkillProvider, ISkillService, IToolRegistry）
- Tests: `test/OpenAgent.Core.Tests/Skill/`
