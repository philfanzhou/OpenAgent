# Agent Skills

OpenAgent 使用 MAF 官方 `AgentSkillsProvider` 提供 Agent Skills。Web 端支持上传 ZIP 或手动填写单文件 Markdown；两者都必须包含一个符合官方 Agent Skills 规范的 `SKILL.md`。ZIP 在 OSS 中按目录文件对象保存，并由索引对象记录路径和哈希；执行时只 materialize 到请求级临时目录，不再重复解压 ZIP。

## Core Capabilities

| Capability | Description |
|---|---|
| 官方格式 | `SKILL.md` YAML frontmatter + Markdown instructions |
| 渐进披露 | MAF 提供 `load_skill` 和 `read_skill_resource` |
| Skill 目录 | PostgreSQL 保存租户范围的 Skill 元数据；Redis `skill:published:index` + `skill:registry:{tenantHash}:{skillId}` 仅作派生缓存 |
| Agent 绑定 | `SkillsConfig` 只从当前 Agent 配置选择已启用 Skill；目录注册不产生绑定 |
| 权限过滤 | 在创建 provider 前按 Agent、Skill 和用户 ACL 过滤 |
| 生命周期 | `AgentExecutionScope` 释放 provider 并删除临时目录 |
| 隔离脚本 | 逐 Skill 授权后，通过显式 runner 发送到独立 Unix Socket 容器 |

## Architecture

```text
POST /skills/packages
        │
        ▼
PostgreSQL SkillDefinitions + MinIO/S3
        │  租户共享对象键 + SkillPackageStorageIndex
        ▼
AgentConfig.Skills → AgentSkillsProviderFactory
        │  租户范围对象文件 → request temp directory
        ▼
MAF AgentSkillsProvider
        │
        ▼
ChatClientAgent.AIContextProviders
```

## Security boundary

Skill 包中的脚本不在 OpenAgent 宿主进程执行。新上传包默认禁止脚本；只有管理员逐 Skill 开启、沙盒健康且脚本通过路径/扩展名/大小/参数复核时，MAF `run_skill_script` 才会调用独立容器。容器基线与多租户限制见 [MCP 与 Skill 执行安全调研](../../../integrations/mcp-skill-execution/RESEARCH.md)。

Skill 只允许本地持久化来源：数据库中的目录元数据和租户对象存储中的 ZIP/MD 展开文件。Skill 对象键使用 `files/tenants/{tenant-hash}/skill-packages/...`，不包含 `users/{user-hash}`；HTTP Endpoint Skill 已移除；Redis 不是事实源，数据库可用时不会用 Redis-only 数据恢复目录。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs`
- Host: `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/AgentSkillPackageArchiveTests.cs`
- Sandbox: `Backend/src/OpenAgent.SkillSandbox.Host/`
