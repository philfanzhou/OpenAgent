# 新功能规划

在 OpenAgent 平台规划新功能时，回答以下问题后再开始编码。

---

## 第一步：确定归属层

新功能属于哪一层？

```
Contracts ← Core ← Engine/Router ← Host
```

| 问题 | 回答 |
|------|------|
| 需要新接口/DTO/错误码？ | → Contracts |
| 需要新的核心能力（新 Engine / 新 Pipeline 中间件）？ | → Core |
| 需要新的 HTTP 端点或后台服务？ | → Engine |
| 需要新的路由/网关策略？ | → Router |
| 需要新的 DI 或认证基础设施？ | → Hosting |
| 只是测试辅助？ | → TestCode |

> 一个功能可能跨多层。从最内层（Contracts）开始定义，向外逐层实现。

---

## 第二步：确认架构影响

- [ ] 是否违反依赖方向？如果功能需要反向引用，重新设计
- [ ] 是否需要新的 `IAgentEngine` 实现？如果要接入新的 LLM 协议
- [ ] 是否需要新的 Pipeline 中间件？中间件注册顺序有严格要求
- [ ] 是否需要新的集成点？（LLM/MCP/RAG/Redis/SQL Server）

---

## 第三步：规划文档更新

| 需更新的文档 | 条件 |
|-------------|------|
| `docs/modules/` | 新增核心能力（MCP/Skill/RAG/Engine） |
| `docs/integrations/` | 新增外部集成 |
| `docs/overview/` | 架构或核心流程变化 |
| `AGENTS.md` | 约定、约束或入口命令变化 |
| `.agent/rules/coding-conventions.md` | 编码规范变化 |
| `.agent/skills/` | 新增开发工作流 |
| `TestCode/docs/` | 测试流程变化 |
| `TestCode/README.md` | 测试环境变化 |

---

## 第四步：规划测试

| 测试类型 | 必须加？ | 加在哪个文件？ |
|---------|---------|---------------|
| xunit 单元测试 | 所有新 Core 逻辑 | `Agent.Core/<Module>.Tests/` |
| MSTest 集成测试 | 跨服务行为 | `TestCode/Agent.TestEngine/` |
| PowerShell E2E | 完整用户流程 | `TestCode/scripts/` |
| 测试数据 | 新数据源 | 通过 Engine API 或 RedisTool 创建到 Redis |

---

## 第五步：实施顺序

推荐从内到外：

```
1. Agent.Contracts — 定义接口、DTO、错误码
2. Agent.Core — 实现核心逻辑
3. Agent.Engine / Agent.Router — 接入宿主
4. TestCode — 测试配置和脚本
5. 文档 — 更新所有相关 doc
```

> 每个步骤构建一次确认编译通过，不要等到最后。

---

## 检查清单（开始编码前）

- [ ] 归属层已确认，不违反依赖方向
- [ ] 新增接口/类名已确定，符合命名规范
- [ ] NuGet 依赖（如有）有明确理由
- [ ] 测试策略已确定（单元 + 集成 + 可选的 E2E）
- [ ] 文档更新范围已确定
- [ ] 现有功能不受影响（回归风险评估）

---

## 参考文件
- 架构概览：`docs/overview/SystemContext.md`
- 核心设计：`docs/overview/Design.md`
- 编码规范：`.agent/rules/coding-conventions.md`
- 代码审查清单：`.agent/prompts/code-review.md`
