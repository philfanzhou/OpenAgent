# .agent — AI 编码工具共享资源

本目录包含面向 AI 编码工具（Claude Code、Copilot、Codex、Cursor、Windsurf 等）的共享资源。所有文件使用**通用 Markdown 格式**，不绑定任何特定工具。

## 目录结构

```
.agent/
├── README.md            ← 本文件
├── rules/               ← 全局编码规则（AI 生成代码时必须遵守）
├── skills/              ← 技能/工作流定义（AI 执行特定任务时读取）
└── prompts/             ← 提示词模板（用户或 AI 主动引用）
```

## 三个目录的分工

| 目录 | 定位 | AI 什么时候读 |
|------|------|-------------|
| `rules/` | 编码时必须遵守的硬性规则 | 每次生成代码时都应参考 |
| `skills/` | 特定任务的分步工作流 | 执行对应任务时按需读取 |
| `prompts/` | 可复用的提示词模板 | 用户或 AI 主动引用时读取 |

## 快速路由

| 任务 | 优先读取 |
|------|----------|
| 新增 LLM 引擎 | `.agent/skills/add-engine.md` |
| 修改 Teams / Outlook / Cron / Playground | `.agent/skills/channels-development.md` |
| 构建和测试 | `.agent/skills/build-and-test.md` |
| 自动选择验证范围 | `.agent/skills/verify-changes.md` |
| 文档整理 | `.agent/skills/update-docs.md` |

当用户要求“排查问题 / 请求无响应 / 查日志 / trace / metrics / Grafana / Loki / Tempo / Prometheus”时，优先读取：

- `.agent/skills/trace-troubleshoot.md`

## 使用方式

### Claude Code
AGENTS.md 会自动加载。技能和提示词可以在对话中引用：
```
请按照 .agent/skills/add-mcp-tool.md 的工作流添加一个新的 MCP 工具
```

### GitHub Copilot / Codex
AGENTS.md 会自动加载。编码规则会在代码生成时参考。

### Cursor / Windsurf
AGENTS.md 会自动加载。可通过 `@.agent/` 引用技能和提示词。

### 通用
任何 AI 工具都可以搜索或引用 `.agent/` 目录下的文件。

## 与项目文档的关系

- **给人看的文档**：根 `README.md`、各模块 `docs/`、`TestCode/docs/`
- **给 AI 看的资源**：本目录 + `AGENTS.md`
- 两者互补，不重复。本目录只放"AI 无法从代码推断"的信息。
