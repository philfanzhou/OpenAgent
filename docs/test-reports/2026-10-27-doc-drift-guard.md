# 文档漂移检查脚本与 CI 门禁接入

日期：2026-10-27；分支 `feat/doc-drift-guard`。背景：PR #251/#252 的清理证明删除符号后文档会系统性滞后
（AgentExecutor、MCP 客户端、Redis 注册表等残留十余处），需要机制防线代替人工记忆。

## 交付

- `scripts/check-doc-drift.py`（仅标准库，Windows/Linux 均可运行）：
  - 禁引清单：19 个已删除类型（ASCII 词边界匹配，中文紧邻可命中，不误伤 `RuntimeAgentExecutor` 等长名前缀
    与 `AgentExecutorUsageTests.cs` 测试文件名）+ 6 个键名/工具命名/包名字面量；
  - 路径有效性：反引号内以 `Backend/`、`Frontend/` 开头的片段必须实际存在；跳过通配、省略号与 `:行号`
    后缀，顿号/逗号分隔的多文件列表逐段检查；
  - 豁免：行尾 `<!-- docguard-allow -->`，仅供「某物已删除」声明行使用。
- CI `docs` job（ubuntu-latest）执行同一脚本，push/PR 均触发。
- `.agent/rules/doc-standards.md` §4.1：删除符号时的三步同步规则（入清单 → 跑脚本 → 必要时加豁免）。

## 实测

- 首次运行（76 个活文档）发现 3 处：
  - `docs/overview/DevelopmentGuide.md:55` — **真实漂移**（引用已删除的 `AgentFactory`），已改写为执行链无关表述；
  - `docs/integrations/opencode-runtime.md:109`、`docs/modules/engine/runtime-execution/README.md:4` — 合法
    「已移除」声明，加豁免标记。
- 修正后复跑：76 个文档、0 问题、退出码 0。
- 豁免标记现存 7 行（AGENTS.md、execution/pipeline、mcp-server ×2、opencode-runtime ×2、runtime-execution），
  全部为删除声明语境；规范文件中的标记为规则示例。

## 范围与限制

- 历史目录（`docs/archive`、`test-reports`、`decisions`、`planning`、`architecture`）不检查：ADR 与测试记录
  合法描述过去状态。
- 仅校验反引号内仓库根路径；相对路径简写（如 `Extensions/Foo.cs`）与 markdown 相对链接不在范围。
- 禁引清单需随符号删除手动维护，规则已写入 doc-standards §4.1。
