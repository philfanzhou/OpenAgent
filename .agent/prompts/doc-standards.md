# 文档规范

对 OpenAgent 仓库文档进行任何操作前，先阅读本规范。

---

## 1. 文档分布总览

```
仓库根
├── AGENTS.md                          ← AI Agent 入口（任务路由 + 速查）
├── .agent/rules/coding-conventions.md ← 编码规范（权威）
├── .agent/skills/                     ← 任务工作流（AI 可执行步骤）
├── .agent/prompts/                    ← 可复用提示模板
│
├── Backend/OpenAgent/
│   ├── Agent.Contracts/               ← Design.md + Req.md（接口层文档）
│   ├── Agent.Core/docs/               ← 最完整的文档集
│   │   ├── overview/                  ← 7 文件：SystemContext, Design, Requirements,
│   │   │                                  KeyFlows, Integration, DataOwnership, README
│   │   ├── modules/                   ← 按功能域拆分，每个功能点 6 件套
│   │   ├── Integration/               ← 外部依赖集成文档（6 件套）
│   │   └── database/                  ← 数据存储文档
│   ├── Agent.Engine/docs/             ← 同 Core 结构（overview + modules + Integration）
│   ├── Agent.Router/docs/             ← 同 Core 结构 + development/ + temp/
│   └── Agent.Hosting/                 ← Design.md + Req.md
│
└── TestCode/
    ├── README.md                      ← 测试环境总览
    └── docs/                          ← E2E / MCP / Skill 测试指南
```

---

## 2. 标准文件模板

### 2.1 功能点 6 件套（modules/ 和 Integration/ 下每个功能点）

每个功能点目录**必须**包含以下 6 个文件。文件编号固定，不可跳过或调换顺序。

| 编号 | 文件名 | 内容要求 | 面向读者 |
|------|--------|---------|---------|
| 01 | `01-FEATURE.md` | 用户故事、功能概述、核心能力表、当前状态（已实现/规划中）、当前限制 | 所有人 |
| 02 | `02-SPEC.md` 或 `02-ARCHITECTURE.md` | **SPEC**：接口签名、方法规格、DI 注册方式。**ARCHITECTURE**：架构图、数据流、连接/执行流程、错误处理、排障指南 | 开发者 |
| 03 | `03-DESIGN.md` 或 `03-DATA-MODELS.md` | **DESIGN**：设计决策、关键源文件路径、架构图、数据转换表。**DATA-MODELS**：数据模型定义、字段表、类型映射 | 开发者 |
| 04 | `04-TASKS.md` 或 `04-API.md` 或 `04-BEHAVIOR.md` | **TASKS**：实现任务拆解、TODO 列表。**API**：端点列表、请求/响应格式。**BEHAVIOR**：运行时行为描述 | 开发者 |
| 05 | `05-TESTS.md` | 测试策略、单元测试场景表、集成测试场景、已知测试缺口 | 测试者 |
| 06 | `06-CONVENTIONS.md` | 命名约定、设计约定、限制、代码示例（如适用） | 维护者 |

**命名选择规则：**

| 模块类型 | 使用模板 |
|----------|---------|
| `modules/capabilities/*` | 01-FEATURE, 02-ARCHITECTURE, 03-DATA-MODELS, 04-API, 05-TESTING, 06-CONVENTIONS |
| `modules/engine/*` | 01-FEATURE, 02-SPEC, 03-DESIGN, 04-TASKS, 05-TESTS, 06-CONVENTIONS |
| `modules/execution/*` | 01-FEATURE, 02-SPEC, 03-DESIGN, 04-TASKS, 05-TESTS, 06-CONVENTIONS |
| `modules/security/*` | 01-FEATURE, 02-SPEC, 03-DESIGN, 04-TASKS, 05-TESTS, 06-CONVENTIONS |
| `Integration/*` | 01-FEATURE, 02-SPEC, 03-DESIGN, 04-TASKS, 05-TESTS, 06-CONVENTIONS |

> **禁止**在同一目录下同时存在新旧两种命名的文件。如有，旧文件要么删除（内容已被新文件覆盖），要么合并后删除。

### 2.2 Overview 7 件套（每个模块的 docs/overview/）

| 文件 | 内容 |
|------|------|
| `README.md` | 文档清单 + 阅读建议 |
| `SystemContext.md` | 服务定位、上下游、参与者 |
| `Design.md` | 项目组成、分层架构、核心抽象、DI 入口 |
| `Requirements.md` | 服务级需求编号体系（R-01 ~ R-NN） |
| `KeyFlows.md` | 关键跨服务时序图与调用链 |
| `Integration.md` | 集成矩阵、接口边界、失败语义 |
| `DataOwnership.md` | 数据主责、引用边界、双写规则 |

---

## 3. 查找文档

### 3.1 按任务类型定位

| 我要做什么 | 先看 |
|-----------|------|
| 了解某个功能的设计和接口 | `modules/<域>/<功能点>/02-SPEC.md` + `03-DESIGN.md` |
| 排查某个功能的运行时问题 | `modules/<域>/<功能点>/02-ARCHITECTURE.md`（含排障指南） |
| 写某个功能的测试 | `modules/<域>/<功能点>/05-TESTS.md` |
| 了解编码约定 | `.agent/rules/coding-conventions.md` |
| 了解集成方式和失败处理 | `<模块>/docs/Integration/<依赖>/` |
| 搭建本地开发环境 | `<模块>/docs/development/LocalSetup.md` |
| 运行 E2E 测试 | `TestCode/docs/e2e-test-guide.md` |

### 3.2 搜索策略

```bash
# 按文件名搜索
find . -name "01-FEATURE.md" -path "*/skill/*"

# 按内容搜索（ripgrep）
rg "IAgentEngine" --glob "*.md"

# 查找某个错误码的所有引用
rg "McpConnectionFailed" --glob "*.md"
```

---

## 4. 新增文档

### 4.1 新增功能点文档

当添加新的功能模块时：

1. 在 `modules/<域>/` 下创建功能点子目录
2. 按模板创建 6 个文件（参考同域已有模块的命名风格）
3. 更新 `modules/<域>/README.md` 添加新条目
4. 更新 `modules/README.md` 添加新条目
5. 如果涉及外部依赖，在 `Integration/` 下创建对应的 6 件套

### 4.2 文件内容要求

- **标题**：`# <类型>: <功能名>`（如 `# Feature: MCP 协议客户端`）
- **交叉引用**：文件末尾必须包含指向同目录其他 5 个文件的链接
  ```markdown
  ## 相关文档
  - [02-SPEC](./02-SPEC.md)
  - [03-DESIGN](./03-DESIGN.md)
  - [04-TASKS](./04-TASKS.md)
  - [05-TESTS](./05-TESTS.md)
  - [06-CONVENTIONS](./06-CONVENTIONS.md)
  ```
- **语言**：所有文档内容必须使用中文（与现有文档保持一致）
- **推断标注**：基于代码推断但未验证的内容，标注 `[推断]` 或 `[待确认]`
- **源文件引用**：关键实现文件应标注路径，如 `` `src/MAF/MafEngine.cs` ``

### 4.3 新增 Integration 文档

```
Integration/<服务名>/
├── 01-FEATURE.md       ← 集成什么、为什么集成
├── 02-SPEC.md          ← 连接配置、接口契约
├── 03-DESIGN.md        ← 连接管理、重试/降级策略
├── 04-TASKS.md         ← 接入步骤、配置清单
├── 05-TESTS.md         ← 集成测试场景
└── 06-CONVENTIONS.md   ← 命名约定、key 模式、超时配置
```

---

## 5. 修正文档

### 5.1 代码变更时同步更新

| 代码变更 | 需要检查的文档 |
|---------|--------------|
| 修改公共接口 | `02-SPEC.md`、`03-DESIGN.md`、`06-CONVENTIONS.md` |
| 新增/修改错误码 | `02-ARCHITECTURE.md`（错误处理章节） |
| 修改 DI 注册 | `02-SPEC.md`、`03-DESIGN.md` |
| 修改中间件顺序 | `03-DESIGN.md`、`06-CONVENTIONS.md` |
| 新增/修改配置项 | `02-SPEC.md`、`Integration/<依赖>/` |
| 修改数据模型 | `03-DATA-MODELS.md` |
| 修改测试策略 | `05-TESTS.md` |

### 5.2 常见文档问题修复

| 问题 | 修复方式 |
|------|---------|
| 双命名文件并存 | 验证新文件内容覆盖旧文件后，删除旧文件 |
| 断链（引用不存在的文件） | 检查目标文件是否改名或移动，更新链接 |
| 旧目录引用（如 `../00-overview/`） | 替换为新路径（如 `../overview/`） |
| `[推断]` 标注的内容已确认 | 移除 `[推断]`，补充确认依据 |
| `[待确认]` 项已明确 | 更新为确定结论或移除 |
| 缺少交叉引用链接 | 在文件末尾补充指向同目录其他文件的链接 |
| 文件内容空洞（< 20 行） | 基于代码补充实际内容 |

---

## 6. 文档质量检查清单

对任何文档变更，检查以下项：

### 结构检查
- [ ] 目录下的文件数是否符合预期（模块功能点 = 6，overview = 7）
- [ ] 文件命名是否与同域其他模块一致
- [ ] 无旧命名文件残留
- [ ] 无空目录

### 内容检查
- [ ] 交叉引用链接有效（指向的文件存在）
- [ ] 代码示例、接口签名与源码一致
- [ ] 推断内容已标注 `[推断]` 或 `[待确认]`
- [ ] 关键源文件路径正确
- [ ] 无明显的复制粘贴错误（如模块名写错）

### 索引检查
- [ ] 上级 `README.md` 包含新增条目的链接
- [ ] `AGENTS.md` 的任务路由表覆盖了新增内容（如适用）
- [ ] TestCode 相关文档在 `TestCode/README.md` 有引用

---

## 7. 禁止事项

- ❌ 在同一目录下创建新旧两种命名的文件
- ❌ 跳过编号（如从 01 直接到 03）
- ❌ 在不同模块中使用不同的文件命名风格
- ❌ 复制大段源码到文档（用路径引用代替）
- ❌ 在文档中写中文注释/字符串（与编码规范一致，文档内容本身除外）
- ❌ 文档间循环引用
- ❌ 在 README.md 中重复功能点文档的详细内容（保持 README 为索引角色）
