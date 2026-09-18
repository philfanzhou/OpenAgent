# Agent Skills

OpenAgent 使用 MAF 官方 `AgentSkillsProvider` 提供 Agent Skills。Web 端支持上传 ZIP 或手动填写单文件 Markdown；两者都必须包含一个符合官方 Agent Skills 规范的 `SKILL.md`。ZIP 在 OSS 中按目录文件对象保存，并由索引对象记录路径和哈希；执行时只 materialize 到请求级临时目录，不再重复解压 ZIP。

## Core Capabilities

| Capability | Description |
|---|---|
| 官方格式 | `SKILL.md` YAML frontmatter + Markdown instructions |
| 渐进披露 | MAF 提供 `load_skill`、`read_skill_resource`，双开关 + 按实例开启后提供 `run_skill_script` |
| Skill 目录 | PostgreSQL 保存租户范围的 Skill 元数据；Redis `skill:published:index` + `skill:registry:{tenantHash}:{skillId}` 仅作派生缓存 |
| Agent 绑定 | `SkillsConfig` 只从当前 Agent 配置选择已启用 Skill；目录注册不产生绑定 |
| 权限过滤 | 在创建 provider 前按 Agent、Skill 和用户 ACL 过滤 |
| 生命周期 | `AgentExecutionScope` 释放 provider 并删除临时目录 |
| 脚本执行 | 默认禁用。宿主 `CodeExecution.Enabled` + Agent `CodeExecution` 绑定 + `SkillInstanceConfig.ScriptExecutionEnabled` 全部开启后，仅 `.py` 脚本经隔离 Runner 执行（详见下文） |

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

Skill 指令加载与资源读取默认启用；包内脚本默认完全不可执行。脚本执行需要三层开关同时开启：宿主 `CodeExecution.Enabled`（隔离 Runner 已部署）、Agent 配置的 `CodeExecution` 绑定、以及 `SkillInstanceConfig.ScriptExecutionEnabled`（按 Skill 实例，默认 false）。未全部开启时 `ScriptFilter` 不披露任何脚本，runner 显式拒绝执行。

开启后脚本也绝不在 Engine 进程内执行：`SkillScriptRunner` 将整个 Skill 包按相对路径全量挂载为沙箱 `/input` 输入（保留包内目录结构，包根与脚本目录进入 `sys.path`，跨目录 import 可解析），经生成的专用 wrapper 入口 `openagent_skill_entry__.py` 以 `runpy` 在 Bubblewrap 沙箱内启动（无网络、非 root、固定 venv），并按会话（conversation）复用 Runner 工作区。约束与 `execute_code` 完全一致：

- 仅 `.py` 脚本；shell 与其他解释器不披露、不执行；
- `ExecutionLimits` 输入文件上限（100 个、单文件 10 MiB、总 20 MiB、安全相对路径名）；包可自带 `main.py`，wrapper 使用专用入口不与之冲突；
- 与 `execute_code` 共享每请求预算（`CodeExecutionBudget`），两条通道无法互相绕过限额；
- 调用时按 `(Tool, run_skill_script)`、`(Function, run_skill_script)` 与 `(Skill, name)` 复核授权；
- 产物经 `FileAssetService` 登记为会话资产，由 `publish_files` 发布；
- 参数以 JSON 传入 wrapper，映射为脚本 `sys.argv`（字符串直传，其余 JSON 编码）。

MAF 的 `run_skill_script` 审批回调被关闭（`DisableRunSkillScriptApproval`），原因是当前 chat 契约无法往返 MAF 审批请求；补偿控制即上述授权复核、扩展名与文件名白名单、共享预算和 Bubblewrap 隔离。

Skill 只允许本地持久化来源：数据库中的目录元数据和租户对象存储中的 ZIP/MD 展开文件。Skill 对象键使用 `files/tenants/{tenant-hash}/skill-packages/...`，不包含 `users/{user-hash}`；HTTP Endpoint Skill 已移除；Redis 不是事实源，数据库可用时不会用 Redis-only 数据恢复目录。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs`、`SkillScriptRunner.cs`
- Host: `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/AgentSkillPackageArchiveTests.cs`、`SkillScriptRunnerTests.cs`、`AgentSkillsProviderFactoryTests.cs`
