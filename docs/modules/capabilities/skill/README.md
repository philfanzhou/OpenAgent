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
| 脚本清单 | 上传时扫描包内 `.py` / `.js` / `.mjs` / `.sh` 文件，把相对路径记录到 `SkillInstanceConfig.ScriptNames`（`ScriptCount` 为派生值）；供管理端展示与开启前审阅 |
| 在线编辑 | `PUT /skills/{skillId}/source` 原地替换 `SKILL.md`，包内脚本与脚本执行开关保持不变 |

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

### 上传 Skill 与脚本执行配置

上传的包被视为不可信代码：上传接口（`POST /api/v1/admin/skills/packages` 与 `POST /api/v1/admin/skills/{agentId}/packages`）接受可选 multipart 字段 `scriptExecutionEnabled`，缺省为 false；即使显式传 true，包内没有可执行脚本（`.py` / `.js` / `.mjs` / `.sh`）也会被 400 拒绝。上传时扫描出的脚本相对路径记入 `ScriptNames` 随实例保存。重新上传（覆盖同名 Skill）会把开关重置为 false——新代码需要重新审阅开启。

已上传的 Skill 通过 `PATCH /api/v1/admin/skills/{skillId}`（body `{ "scriptExecutionEnabled": bool }`）开启或关闭：服务端从对象存储重新读取包清单校验脚本存在（旧包没有 `ScriptNames` 记录也能正确处理），刷新清单后写回目录。Chat 前端在 Skill 目录表格中展示脚本数量与清单，并在开启时弹出信任确认；Agent 绑定选择器同时展示脚本数。该开关只管理披露面；运行时仍受上述三层开关、沙箱约束与授权复核限制。

### 在线编辑 Skill 内容

`PUT /api/v1/admin/skills/{skillId}/source`（body `{ "markdown": string }`）原地替换已有 Skill 的 `SKILL.md`：包内其余文件（含脚本）原样重存到新包，`ScriptExecutionEnabled` 与脚本清单保持不变——改描述不需要重新授权脚本。frontmatter `name` 不允许改变（改名需重新上传），否则 400。Chat 前端编辑已有 Skill 保存时即走该端点，不再整包重传。

开启后脚本也绝不在 Engine 进程内执行：`SkillScriptRunner` 将整个 Skill 包按相对路径全量挂载为沙箱 `/input` 输入（保留包内目录结构，Python 包根与脚本目录进入 `sys.path`，跨目录 import 可解析），经生成的专用 wrapper 入口（`.py` → `openagent_skill_entry__.py` 以 `runpy` 启动；`.js` / `.mjs` → `openagent_skill_entry__.mjs` 以动态 `import` 启动；`.sh` → `openagent_skill_entry__.sh` 以 `exec /bin/bash` 启动）在 Bubblewrap 沙箱内运行（无网络、非 root、固定 venv / Node / bash），并按会话（conversation）复用 Runner 工作区（挂载输入跨调用保留，`/work`、`/output` 按调用隔离）。约束与 `execute_code` 完全一致：

- 仅 `.py`、`.js`、`.mjs`、`.sh` 脚本（与 Runner 的 Python / JavaScript / shell 入口一致）；其他解释器不披露、不执行；
  `execute_code` 工具的 schema 仍只声明 python / javascript，shell 目前仅经 skill 脚本通道使用；
- `ExecutionLimits` 输入文件上限（100 个、单文件 10 MiB、总 20 MiB、安全相对路径名）；包可自带 `main.py` / `main.mjs` / `main.sh`，wrapper 使用专用入口不与之冲突；
- 调用时按 `(Tool, run_skill_script)`、`(Function, run_skill_script)` 与 `(Skill, name)` 复核授权；
- 产物经 `FileAssetService` 登记为会话资产，由 `publish_files` 发布；
- 参数以 JSON 传入 wrapper：Python 映射为 `sys.argv`、JavaScript 映射为 `process.argv`、shell 映射为位置参数（单引号安全转义；字符串直传，其余 JSON 编码）。`.js` 以 CommonJS 语义加载，ESM 请使用 `.mjs`；`.sh` 以 bash 运行。

MAF 的 `run_skill_script` 审批回调被关闭（`DisableRunSkillScriptApproval`），原因是当前 chat 契约无法往返 MAF 审批请求；补偿控制即上述授权复核、扩展名与文件名白名单、共享预算和 Bubblewrap 隔离。

Skill 只允许本地持久化来源：数据库中的目录元数据和租户对象存储中的 ZIP/MD 展开文件。Skill 对象键使用 `files/tenants/{tenant-hash}/skill-packages/...`，不包含 `users/{user-hash}`；HTTP Endpoint Skill 已移除；Redis 不是事实源，数据库可用时不会用 Redis-only 数据恢复目录。

## Source

- Core: `Backend/src/OpenAgent.Core/Capabilities/Skill/AgentSkillsProviderFactory.cs`、`SkillScriptRunner.cs`
- Host: `Backend/src/OpenAgent.Engine.Host/Skills/SkillPackageManagementService.cs`、`Backend/src/OpenAgent.Engine.Host/Extensions/ManagementEndpointExtensions.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Capabilities/AgentSkillPackageArchiveTests.cs`、`SkillScriptRunnerTests.cs`、`AgentSkillsProviderFactoryTests.cs`、`Backend/tests/OpenAgent.Engine.Tests/Skills/SkillPackageManagementServiceTests.cs`
