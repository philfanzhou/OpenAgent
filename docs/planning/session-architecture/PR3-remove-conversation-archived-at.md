# PR-3 报告：移除会话 ArchivedAt 预留字段（ADR-0004）

- 分支：`docs/adr-conversation-archived-at`（基于 main@9b694ab）
- 关联决策：[ADR-0004](../../decisions/0004-Conversation-Archive-Semantics.md)
- 类型：行为无变化的清理 + 决策文档

## 动机

2026-09 会话架构审查发现 `ConversationRecord.ArchivedAt` 是误导性死代码：

| 证据 | 位置 | 问题 |
|------|------|------|
| 字段定义 | `Backend/src/OpenAgent.Contracts/Conversation/ConversationRecord.cs:35-38`（改动前） | 注释承诺"数据分层迁移判断"，默认值 `DateTimeOffset.UtcNow` 使每条会话**创建即归档**，语义自相矛盾 |
| 无持久化 | `Backend/src/OpenAgent.Infrastructure/Entities/ConversationEntity.cs` | EF 实体无该列，不落 PostgreSQL |
| 无消费者 | 全仓 grep（src/tests/前端/docs） | 唯一引用是 `InMemoryConversationStore.cs:217` 的测试用字段复制 |
| 无实现 | — | 仓库不存在任何归档任务/归档表/保留周期策略 |

对照已实现的软删除语义（`IsDeletedByUser` + 可空 `DeletedAt` + EF 映射 + SoftDeleteAsync），该字段让读者误以为存在数据分层能力。审查报告将其列为 P2 清理项。

## 改动清单

| 文件 | 改动 |
|------|------|
| `Backend/src/OpenAgent.Contracts/Conversation/ConversationRecord.cs` | 删除 `ArchivedAt` 属性及其 XML 注释 |
| `Backend/src/OpenAgent.Core/Conversation/Store/InMemoryConversationStore.cs` | 删除 ToRecord 映射中的 `ArchivedAt = r.ArchivedAt` 一行 |
| `docs/decisions/0004-Conversation-Archive-Semantics.md` | 新增 ADR：删除决策 + 未来归档能力的设计约束（可空时间戳、先实现后字段、YAGNI） |
| `docs/decisions/README.md` | ADR 索引新增 0004 条目 |
| `docs/planning/README.md`、`docs/planning/session-architecture/README.md` | 建立重构系列导航 |

## 行为变化

**无。** 该字段从未持久化、从未被读取。唯一外部可见影响：

- `ConversationRecord` 的 Redis JSON 序列化产物少一个字段。已缓存的旧 JSON 反序列化不受影响（System.Text.Json 默认忽略未知属性），TTL 过期后自然消失。
- Contracts 程序集公共表面缩小一个属性；全仓无编译期消费者（已 grep 验证 src 与 tests）。

## 测试证据

- 基线（main@9b694ab）：638 通过 / 0 失败 / 22 跳过
- 本 PR：`dotnet build Backend/OpenAgent.sln` 0 错误；`dotnet test Backend/OpenAgent.sln` 638 通过 / 0 失败 / 22 跳过（无测试引用被删字段，结果与基线一致）
- 详见 `docs/test-reports/`（若有归档要求）；本 PR 为纯删除，不需要新增测试

## 风险与回滚

- 风险：极低。无行为变化、无持久化影响、无消费者。
- 回滚：单 commit revert 即可恢复字段（若未来恢复，遵循 ADR-0004 的设计约束：可空 + 由归档动作写入）。

## 与系列的衔接

本 PR 同时建立 `docs/planning/session-architecture/` 目录与系列 README，后续 PR-1/2/4/5/6 的报告将加入同一目录并更新导航表。
