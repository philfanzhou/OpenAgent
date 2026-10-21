# OpenCode 运行时重构实测记录（MAF 移除 / 模型配置 / MCP 透传 / Skill 物化）

验证日期：2026-10-20。基于分支 `codex/acp-opencode-runtime`（基线 `e41d2aaa`）上的重构提交：`refactor(application): remove MAF inference stack`、`feat(runtime): per-agent opencode model configuration`、`feat(runtime): pass enabled mcp servers over acp session/new`、`feat(runtime): materialize skills into per-turn workspace via links`、`feat(web): opencode runtime settings in agent editor`。环境：Windows x64（控制台代码页 936）、.NET SDK 10。本记录只描述本次实测；任务状态见 [任务清单](../planning/TODO.md)。

## 结果

| 检查 | 实际结果 |
|---|---|
| `dotnet build Backend/OpenAgent.sln` | 0 错误 |
| `dotnet test Backend/OpenAgent.sln` | Contracts 58、Architecture 14、Runtime 9（+5 门控跳过）、Runner 36（+22 跳过）、Framework 32、Application 262（+2 跳过）、Capabilities 153、Router 124、Engine 213（+1 跳过）全部通过 |
| OpenAgent.Runtime.Acp.Tests | 119 通过、2 失败 —— 与基线一致的既有本机代码页（936）环境失败（terminal UTF-8 断言与控制台场景），见 [2026-10-19 记录](2026-10-19-opencode-runtime.md)，非本次改动引入 |
| 前端 `vue-tsc --noEmit` | 0 错误 |
| 前端 `vitest run` | 18 个文件、153 个测试全部通过 |

## 本次新增/改写的覆盖

- 模型解析链：内嵌 OpenCode 模型（空 llmProfileId 回落、请求显式选择覆盖、两者皆无报错、上下文窗口回退 128000）——`AgentRuntimeResolverTests` 新增 3 例。
- MCP 定义解析：内联/注册表解析、去重、租户过滤、逐 server 授权拒绝 —— `McpFeatureFactoryTests`（替代原 McpToolFactory/McpClientPool/ResourcePipeline 测试，随平台侧代理删除）。
- Skill 物化：工作区 `.opencode/skills/<name>` 链接/复制、路径逃逸拒绝、缺失目录报错 —— `OpenCodeSkillsTests`；session/new MCP 条目映射 —— `OpenCodeSessionMcpTests`。
- 执行器/压缩/SSE 链路全部改为脚本化驱动替身（`ScriptedRuntimeDriver`/`ToolInvokingDriver`，含并行工具调用模式），保留工具隔离/超时/并发、取消持久化、压缩审计语义、SSE 帧、错误分类与压缩端点回归。
- Skill 工具：`read_skill_file`/`run_skill_script`（Runner 路径、包目录限制、三重开关矩阵）——`AgentSkillsProviderFactoryTests` 重写。

## 未验证范围

- 真实 opencode 进程场景（`OPENAGENT_TEST_OPENCODE=1` 门控）本轮未执行：MCP 透传后 OpenCode 对 session/new 远程 server 的实际连接与工具命名/放行、skill 原生发现与加载、内嵌模型的实际推理，需真实 harness 验收后另行记录。
- OpenAI Responses / Anthropic Messages 两种摘要映射（MAF provider 扩展移除后由 ModelBindingChatClient 绑定）尚无对应模型互操作实测。
- MCP server 鉴权头透传未实现（当前平台配置仅 URL/类型）。
