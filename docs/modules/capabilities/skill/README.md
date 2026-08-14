# Skill

Skill 是 Agent.Core 中模型可调用的执行能力，用于承载明确的动作操作。进程内技能由 `SkillRegistry` 保存；上传的 Skill 包由对象存储保存，`SkillCapabilitySource` 只加载当前 Agent 配置已启用的技能。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 技能发现 | 从 `SkillRegistry` 获取已注册的本地和动态技能 |
| 配置过滤 | 根据 `SkillsConfig.EnabledSkills` / `Instances` 过滤 |
| 权限过滤 | 基于用户上下文 ACL（AllowedUserIds / Groups / TenantIds / Roles）|
| 执行路由 | `SkillCapabilitySource` 直接调用 `SkillRegistry` |
| 动态注册 | 通过 `IToolRegistry.RegisterTool` 注册 |
| 多格式包 | JSON、YAML、带 YAML front matter 的 Markdown、包含 manifest 的 ZIP |
| 对象存储 | 上传保存原始包；发现时读取并校验 SHA-256 后构造 HTTP Skill |

## Source Layers
| Source | SkillSource |
|--------|-------------|
| 本地 Skill | `Local`（IToolRegistry + ISkill）|
| MCP 外部工具 | `Mcp` |
| Matrix 平台 | `Matrix` |

## Current Status
**Implemented** — 技能发现、配置/ACL 过滤、对象存储包加载与 HTTP 执行均在 capability source 链路内完成。Skill 绑定属于 Agent 配置的一部分，由 `/api/v1/admin/agents/{agentId}/config` 统一保存；上传/删除对象存储包只使用明确的 `agentId` 管理对应 Agent 的包生命周期。配置保存后驱逐当前 Agent 配置快照，下一次执行立即重新加载挂载。

## Limits
- 无技能调用配额控制（`SkillQuotaExceeded` 错误码已定义但未使用）
- 无技能参数验证链路（`SkillValidationFailed` 错误码已定义但未使用）
- YAML 仅支持 manifest 的顶层标量字段；复杂 Schema 建议使用 JSON、Markdown 或 ZIP 中的 JSON manifest

## Source
- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/SkillCapabilitySource.cs`, `SkillPackageReader.cs`, `ObjectStoredSkillProvider.cs`
- Host: `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`
- Contracts: `Backend/src/OpenAgent.Core/Abstract/IToolRegistry.cs`, `Backend/src/OpenAgent.Contracts/Skills/ISkill.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/SkillCapabilitySourceTests.cs`, `SkillPackageReaderTests.cs`
