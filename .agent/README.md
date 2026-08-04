# .agent — AI 编码工具共享资源

本目录包含面向 AI 编码工具（Claude Code、Copilot、Codex、Cursor、Windsurf 等）的共享资源。所有文件使用**通用 Markdown 格式**，不绑定任何特定工具。

## 目录结构

```
.agent/
├── README.md            ← 本文件
├── rules/               ← 全局编码规则与文档规范（AI 生成代码时必须遵守）
├── skills/              ← 技能/工作流定义（AI 执行特定任务时读取）
└── prompts/             ← 提示词模板（用户或 AI 主动引用）
```

## 三个目录的分工

| 目录 | 定位 | AI 什么时候读 |
|------|------|-------------|
| `rules/` | 编码时必须遵守的硬性规则 | 每次生成代码时都应参考 |
| `skills/` | 特定任务的分步工作流 | 执行对应任务时按需读取 |
| `prompts/` | 可复用的提示词模板 | 用户或 AI 主动引用时读取 |

## 与项目文档的关系

- **给人看的文档**：根 `README.md`、`docs/`、`TestCode/docs/`
- **给 AI 看的资源**：本目录 + `AGENTS.md`
- 两者互补，不重复。本目录只放"AI 无法从代码推断"的信息。

## 任务路由

详细任务路由见 `AGENTS.md`。本目录下的技能文件按需引用：

```
.agent/skills/
├── add-agent-skill.md      ← 添加 Agent 技能
├── add-engine.md           ← 添加新引擎
├── add-llm-provider.md     ← 添加 LLM 提供商
├── add-mcp-tool.md         ← 添加 MCP 工具
├── build-and-test.md       ← 构建与测试
├── channels-development.md ← Channels 开发
├── create-agent-config.md  ← 创建 Agent 配置
├── e2e-test.md             ← E2E 测试
├── service-lifecycle.md    ← 服务生命周期
├── trace-troubleshoot.md   ← Trace/Log/Metrics 排查
├── update-docs.md          ← 文档整理
└── verify-changes.md       ← 验证变更
```
