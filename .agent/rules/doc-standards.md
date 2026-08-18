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
├── docs/                              ← 统一文档中心
│   ├── overview/                      ← 全局总览（系统上下文、设计、流程、数据）
│   ├── modules/                       ← 按功能域组织的详细文档
│   ├── integrations/                  ← 外部依赖集成文档
│   ├── database/                      ← 数据存储唯一事实源
│   ├── decisions/                     ← ADR 归档（已决策的技术选型）
│   ├── planning/                      ← 规划文档（目标架构、重构基线）
│   ├── superpowers/                   ← 计划与设计规格（superpowers 工作流产物）
│   ├── reviews/                       ← 变更 Review 与风险记录
│   └── test-reports/                  ← 测试报告与验证记录
│
└── Backend/
    ├── src/
    │   ├── OpenAgent.Contracts/       ← 源码（无散落文档）
    │   ├── OpenAgent.Core/            ← 源码（无散落文档）
    │   ├── OpenAgent.Engine/          ← 源码（无散落文档）
    │   ├── OpenAgent.Engine.Host/     ← 源码（无散落文档）
    │   ├── OpenAgent.Hosting/         ← 源码（无散落文档）
    │   ├── OpenAgent.Infrastructure/  ← 源码（无散落文档）
    │   └── OpenAgent.Router/          ← 源码（无散落文档）
    └── tests/                         ← 测试项目
```

> **原则**：源码目录只放代码，所有正式文档统一收入 `docs/`。

---

## 2. 文档风格

### 2.1 文件命名

- 功能点目录使用 kebab-case（如 `config-hot-reload`、`tool-calling`）
- 功能点内的文档使用简短标题式命名（如 `DESIGN.md`、`CONVENTIONS.md`），不再强制 6 件套编号
- 上层目录的 `README.md` 必须作为该目录的导航索引

### 2.2 内容原则

- **单一事实源**：每个知识点只在一处描述，其他位置用链接引用
- **源文件引用**：关键实现标注路径 `` `Backend/src/OpenAgent.Core/Foo.cs` ``
- **推断标注**：基于代码推断但未验证的内容，标注 `[推断]` 或 `[待确认]`
- **交叉引用**：相关文档间使用相对路径链接
- **语言**：文档内容使用中文；代码注释和字符串使用英文（与编码规范一致）

### 2.3 README 职责

- 每个 `README.md` 只承担**导航索引**角色
- 不重复下级文档的详细内容
- 包含文档清单表格和阅读建议

---

## 3. 新增文档

1. 确定文档类型（功能设计 / 集成 / 总览 / 决策归档）
2. 放入对应 `docs/` 子目录
3. 更新上级 `README.md` 添加导航条目
4. 如涉及 AI 任务路由，更新 `AGENTS.md`
5. 如涉及编码规范，更新 `.agent/rules/coding-conventions.md`

---

## 4. 修正文档

| 代码变更 | 需要检查的文档 |
|---------|--------------|
| 修改公共接口 | 对应 `DESIGN.md` / `SPEC.md` |
| 新增/修改错误码 | 对应模块的错误处理文档 |
| 修改 DI 注册 | 对应模块的 `DESIGN.md` |
| 修改中间件顺序 | 对应模块的 `CONVENTIONS.md` |
| 新增/修改配置项 | 对应集成文档 |
| 修改数据模型 | `docs/database/` |

---

## 5. 禁止事项

- ❌ 源码目录散落 `.md` 文档（一律收入 `docs/`）
- ❌ 同一知识点在多处重复描述（应单一事实源 + 交叉引用）
- ❌ `README.md` 重复下级文档内容
- ❌ 复制大段源码到文档（用路径引用代替）
- ❌ 文档间循环引用
