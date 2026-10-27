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
│   ├── planning/                      ← 任务清单 TODO 与架构提案
│   └── operations/                    ← 部署、排障与并行预览
│
└── Backend/
    ├── src/
    │   ├── OpenAgent.Contracts/       ← 源码（无散落文档）
    │   ├── OpenAgent.Application/     ← 源码（无散落文档）
    │   ├── OpenAgent.Capabilities/    ← 源码（无散落文档）
    │   ├── OpenAgent.Engine/          ← 源码（无散落文档）
    │   ├── OpenAgent.Engine.Host/     ← 源码（无散落文档）
    │   ├── OpenAgent.Runner/          ← 源码（无散落文档）
    │   ├── OpenAgent.Framework/       ← 源码（无散落文档）
    │   └── OpenAgent.Router/          ← 源码（无散落文档）
    └── tests/                         ← 测试项目
```

> **原则**：源码目录只放代码，所有正式文档统一收入 `docs/`。

---

## 2. 文档风格

### 2.1 文件与目录命名

- `docs/modules/` 每个业务域一份 kebab-case 文档（如 `execution.md`、`capabilities.md`），域内功能点以章节组织；新增功能点优先并入域文档，只有显著扩张的主题才拆分独立文件
- 其余目录（integrations、database、operations 等）单文档主题直接放文件；确有多份文档的主题才建目录，目录名同样使用 kebab-case
- 上层目录的 `README.md` 必须作为该目录的导航索引

### 2.2 内容原则

- **单一事实源**：每个知识点只在一处描述，其他位置用链接引用
- **任务状态**：当前待办只维护在 `docs/planning/TODO.md`，写明优先级、负责人/状态、拥有范围、依赖与验收证据；已有 PR 不等于已完成
- **不留归档**：过时文档先把独特内容并入当前文档再直接删除（git 历史可追溯），不维护归档目录
- **不再新增的类型**：不新增 ADR 文件——重大决策的理由与结论写入对应总览/模块文档，任务期决策记录在 TODO 与实现 PR；不新增独立测试报告文件——验收证据优先链接 PR 与 CI 运行，CI 覆盖外的本地实测事实并入对应集成/模块文档的验证小节；历史 ADR 与测试报告已随文档缩减删除
- **合并再删除**：重复说明先将独特内容并入当前文档，再删除冗余文件并更新引用；部署详情统一在 `docs/operations/deployment.md`
- **源文件引用**：关键实现标注路径 `` `Backend/src/OpenAgent.Application/Foo.cs` ``
- **推断标注**：基于代码推断但未验证的内容，标注 `[推断]` 或 `[待确认]`
- **交叉引用**：相关文档间使用相对路径链接
- **语言**：文档内容使用中文；代码注释和字符串使用英文（与编码规范一致）

### 2.3 README 职责

- 每个 `README.md` 只承担**导航索引**角色
- 不重复下级文档的详细内容
- 包含文档清单表格和阅读建议

---

## 3. 新增文档

1. 确定文档类型（功能设计 / 集成 / 总览 / 运维）
2. 放入对应 `docs/` 子目录
3. 更新上级 `README.md` 添加导航条目
4. 如涉及 AI 任务路由，更新 `AGENTS.md`
5. 如涉及编码规范，更新 `.agent/rules/coding-conventions.md`

---

## 4. 修正文档

| 代码变更 | 需要检查的文档 |
|---------|--------------|
| 修改公共接口 | 对应模块域文档（`docs/modules/<domain>.md`） |
| 新增/修改错误码 | 对应模块域文档的错误处理章节 |
| 修改 DI 注册 | 对应模块域文档 |
| 修改中间件顺序 | 对应模块域文档 |
| 新增/修改配置项 | 对应集成文档 |
| 修改数据模型 | `docs/database/` |

---

## 5. 禁止事项

- ❌ 源码目录散落 `.md` 文档（一律收入 `docs/`）
- ❌ 同一知识点在多处重复描述（应单一事实源 + 交叉引用）
- ❌ `README.md` 重复下级文档内容
- ❌ 复制大段源码到文档（用路径引用代替）
- ❌ 文档间循环引用
