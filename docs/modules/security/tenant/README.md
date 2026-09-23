# 租户架构

本目录定义 OpenAgent 的租户概念、安全边界和演进路径。设计状态为 **Proposed**，在对应迁移阶段完成前，不能把目标模型描述为当前已实现行为。

## 文档清单

| 文档 | 内容 |
|------|------|
| [DESIGN.md](./DESIGN.md) | 租户定义、生命周期、成员、角色、身份来源和请求上下文 |
| [BOUNDARIES.md](./BOUNDARIES.md) | 资源归属与共享、组件职责、资源/缓存键、存储、审计和未来 Channel 边界 |
| [CURRENT-STATE.md](./CURRENT-STATE.md) | Router、Engine、各资源与前端链路的当前实现证据和目标差距 |
| [MIGRATION.md](./MIGRATION.md) | 分阶段迁移、兼容发布、风险、验收门槛与后续 PR 拆分 |

## 阅读建议

1. 先阅读 `DESIGN.md` 和 `BOUNDARIES.md` 了解目标模型。
2. 用 `CURRENT-STATE.md` 对照当前代码，再按 `MIGRATION.md` 的前置关系拆分 PR。
3. 当前认证行为以 [auth](../auth/) 文档和源码为准；目标规则在迁移完成后才成为已实现行为。
