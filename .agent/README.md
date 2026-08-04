# .agent — AI 编码工具共享资源

本目录包含面向 AI 编码工具（Claude Code、Copilot、Codex、Cursor、Windsurf 等）的共享资源。所有文件使用**通用 Markdown 格式**，不绑定任何特定工具。

## 目录结构

```
.agent/
├── README.md                 ← 本文件
├── rules/                    ← 规则（编码规范、文档规范、开发指南）
└── skills/                   ← 技能/工作流（AI 执行特定任务时读取）
```

## 两个目录的分工

| 目录 | 定位 | AI 什么时候读 |
|------|------|-------------|
| `rules/` | 编码时必须遵守的硬性规则 | 每次生成代码时都应参考 |
| `skills/` | 特定任务的分步工作流 | 执行对应任务时按需读取 |

## 与项目文档的关系

- **给人看的文档**：根 `README.md`、`docs/`、`TestCode/docs/`
- **给 AI 看的资源**：本目录 + `AGENTS.md`
- 两者互补，不重复。本目录只放"AI 无法从代码推断"的信息。

## 文件索引

```
.agent/rules/
├── coding-conventions.md     ← 权威编码规范（.NET 版本、依赖、命名、DI、日志）
├── doc-standards.md          ← 文档风格与新增指南
└── development-guide.md      ← 代码审查、功能规划、测试编写、集成排查

.agent/skills/
├── build-and-test.md         ← 构建与测试命令
├── channels-development.md   ← Channels 开发约束
├── e2e-test.md               ← E2E 测试 + 服务生命周期
└── update-docs.md            ← 文档整理
```

## 任务路由

详细任务路由见 `AGENTS.md`。
