# Agent Skills

OpenAgent 使用 MAF 官方 `AgentSkillsProvider` 提供 Agent Skills。Web 端支持上传 ZIP 或手动填写单文件 Markdown；两者都必须包含一个符合官方 Agent Skills 规范的 `SKILL.md`。ZIP 在 OSS 中按目录文件对象保存，并由索引对象记录路径和哈希；执行时只 materialize 到请求级临时目录，不再重复解压 ZIP。

## Core Capabilities

| Capability | Description |
|---|---|
| 官方格式 | `SKILL.md` YAML frontmatter + Markdown instructions |
| 渐进披露 | MAF 提供 `load_skill` 和 `read_skill_resource` |
| Skill 目录 | Redis `skill:published:index` + `skill:registry:{skillId}` 保存可发现的 Skill 元数据 |
| Agent 绑定 | `SkillsConfig` 只从当前 Agent 配置选择已启用 Skill；目录注册不产生绑定 |
| 权限过滤 | 在创建 provider 前按 Agent、Skill 和用户 ACL 过滤 |
| 生命周期 | `AgentExecutionScope` 释放 provider 并删除临时目录 |
| 脚本边界 | 文件源不发现脚本，显式 runner 拒绝所有脚本执行 |

## Architecture

```text
AgentConfig.Skills
        │
        ▼
AgentSkillsProviderFactory
        │  Redis AgentConfig 绑定 + OSS 文件对象 → request temp directory
        ▼
MAF AgentSkillsProvider
        │
        ▼
ChatClientAgent.AIContextProviders
```

## Security boundary

当前集成只启用 Skill 指令加载与资源读取。文件源通过 `ScriptFilter` 隐藏所有脚本，并注册显式拒绝的 runner，避免上传包中的代码在 OpenAgent 宿主进程执行；脚本执行及其隔离方案不在当前能力范围内。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs`
- Host: `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/AgentSkillPackageArchiveTests.cs`
