# 文档规范

对 OpenAgent 仓库文档进行任何操作前，先阅读本规范。

---

## 1. 文档分布

```
仓库根
├── AGENTS.md                          ← AI Agent 入口（任务路由 + 速查）
├── README.md                          ← 项目介绍（给人看）
├── .agent/rules/coding-conventions.md ← 编码规范（权威）
├── .agent/rules/doc-standards.md     ← 本文件（文档规范）
├── .agent/skills/                     ← 任务工作流
│
├── docs/                              ← 统一文档中心（扁平结构，一域一文件）
│   ├── README.md                      ← 总导航
│   ├── database.md                    ← 数据存储唯一事实源（表、字段、索引、迁移）
│   ├── trace-troubleshoot.md          ← Trace/Logs/Metrics 排障手册
│   ├── parallel-previews.md           ← 并行 worktree 预览实例机制
│   ├── overview/                      ← 全局总览（系统上下文、设计、流程、数据）
│   ├── modules/                       ← 功能域文档：engine/capabilities/execution/
│   │                                    security/conversation/router/chat-workspace
│   ├── integrations/                  ← 外部依赖集成：llm-provider/mcp/rag/keycloak/
│   │                                    redis-engine/matrix/agent-provider/file-assets/
│   │                                    code-runner（均为扁平 md）
│   ├── decisions/                     ← ADR 归档（已决策的技术选型）
│   └── planning/                      ← 进行中的规划
│
└── Backend/
    ├── src/                           ← 源码（无散落文档）
    └── tests/                         ← 测试项目
```

> **原则**：
>
> - 源码目录只放代码，所有正式文档统一收入 `docs/`。
> - **一域一文件**：模块文档按功能域写成单个 md（如 `modules/engine.md`），用 `## 小节` 组织子主题；不再使用"每个功能点一个目录 + README 导航"的多层结构。
> - **集成文档扁平命名**：`integrations/<name>.md`，不建子目录。
> - **已完成工作不留文档**：已合并 PR 与已完成重构不保留单独的规划/测试报告/验收文档（PR 描述即报告）；`planning/` 只放进行中的规划，完成后删除或收缩为索引行。
> - 导航职责统一由 `docs/README.md` 承担，子目录不再放纯导航 README。

---

## 2. 文档风格

### 2.1 文件命名

- 文档文件使用 kebab-case（如 `config-hot-reload`、`tool-calling`）
- 模块文档为 `<域>.md` 单文件（如 `modules/engine.md`），子主题用 `## 小节` 组织
- 子主题小节标题保持稳定，便于其他文档锚点引用

### 2.2 内容原则

- **单一事实源**：每个知识点只在一处描述，其他位置用链接引用
- **源文件引用**：关键实现标注路径 `` `Backend/src/OpenAgent.Core/Foo.cs` ``
- **推断标注**：基于代码推断但未验证的内容，标注 `[推断]` 或 `[待确认]`
- **交叉引用**：相关文档间使用相对路径链接
- **语言**：文档内容使用中文；代码注释和字符串使用英文（与编码规范一致）

### 2.3 导航职责

- `docs/README.md` 是唯一总导航：每目录一段、每文件一个链接，无多行表格
- 子目录不放纯导航 README；`decisions/`、`planning/` 保留轻量索引 README

---

## 3. 新增文档

1. 确定文档类型（功能设计 / 集成 / 总览 / 决策归档）
2. 模块内容并入对应域文件的新小节，或在 `modules/` 新建 `<域>.md`；集成文档新建 `integrations/<name>.md`
3. 更新 `docs/README.md` 导航条目
4. 如涉及 AI 任务路由，更新 `AGENTS.md`
5. 如涉及编码规范，更新 `.agent/rules/coding-conventions.md`

---

## 4. 修正文档

| 代码变更 | 需要检查的文档 |
|---------|--------------|
| 修改公共接口 | 对应模块域文件的相关小节 |
| 新增/修改错误码 | 对应模块的错误处理小节 |
| 修改 DI 注册 | 对应模块域文件 |
| 修改中间件顺序 | 对应模块域文件 |
| 新增/修改配置项 | 对应集成文档 |
| 修改数据模型 | `docs/database.md` |

---

## 5. 禁止事项

- ❌ 源码目录散落 `.md` 文档（一律收入 `docs/`）
- ❌ 同一知识点在多处重复描述（应单一事实源 + 交叉引用）
- ❌ 为已完成工作新建规划/测试报告/验收文档（PR 描述即报告）
- ❌ 在 `docs/` 内新建纯导航 README 或深层功能点目录
- ❌ 复制大段源码到文档（用路径引用代替）
- ❌ 文档间循环引用
