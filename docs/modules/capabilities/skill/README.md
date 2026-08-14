# Agent Skills

OpenAgent 使用 MAF 官方 `AgentSkillsProvider` 提供 Agent Skills。上传内容必须是 ZIP，包内包含一个符合官方 Agent Skills 规范的 `SKILL.md` 目录；包字节保存在对象存储，执行时解压到请求级临时目录。

## Core Capabilities

| Capability | Description |
|---|---|
| 官方格式 | `SKILL.md` YAML frontmatter + Markdown instructions |
| 渐进披露 | MAF 提供 `load_skill` 和 `read_skill_resource` |
| Agent 绑定 | `SkillsConfig` 只从当前 Agent 配置选择已启用包 |
| 权限过滤 | 在创建 provider 前按 Agent、Skill 和用户 ACL 过滤 |
| 生命周期 | `AgentExecutionScope` 释放 provider 并删除临时目录 |

## Architecture

```text
AgentConfig.Skills
        │
        ▼
AgentSkillsProviderFactory
        │  object storage → request temp directory
        ▼
MAF AgentSkillsProvider
        │
        ▼
ChatClientAgent.AIContextProviders
```

## Security boundary

Skill 包中的脚本不在 OpenAgent 宿主进程执行。MAF 的 file source 使用显式 script runner；当前没有配置脚本 runner，因此上传脚本不会被广告或执行，后续应接入独立沙箱后再开放。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs`
- Host: `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/AgentSkillPackageArchiveTests.cs`
