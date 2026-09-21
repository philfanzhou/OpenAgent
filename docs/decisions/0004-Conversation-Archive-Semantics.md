# ADR-0004：移除会话 ArchivedAt 预留字段，归档语义延后设计

## 状态

已决策，已实现（字段删除，无行为变化）。

## 背景

`ConversationRecord.ArchivedAt` 是一个"预留未实现"的字段：

- 注释声称"用于数据分层迁移判断（超过保留周期则迁移到归档表）"，但仓库内不存在任何归档任务、归档表或保留周期策略；
- 默认值 `DateTimeOffset.UtcNow` 使每条会话在**创建时即被标记为已归档**，语义自相矛盾（对比 `DeletedAt` 的可空设计）；
- 未映射到 EF Core 实体、不落 PostgreSQL、无 Redis 消费者、前端不展示；唯一引用点是 `InMemoryConversationStore` 的字段复制（仅测试使用）。

该字段在 2026-09 会话架构审查中被识别为误导性死代码：读者会误以为系统存在数据分层能力，与 `IsDeletedByUser`/`DeletedAt`（已实现的软删除语义）形成错误对照。

## 决策

1. **删除 `ConversationRecord.ArchivedAt` 属性**及其全部复制点。会话生命周期以已实现的机制为准：软删除（`IsDeletedByUser` + `DeletedAt`，见 `docs/database.md`）与 Redis 热副本 TTL 过期。
2. **归档/数据分层能力遵循 YAGNI，不预留字段**。当真实需求出现（合规保留期、冷数据分层、存储成本控制）时，按以下原则重新设计：
   - 时间戳必须可空、仅由归档动作写入（与 `DeletedAt` 同构），不得带"创建即赋值"的默认值；
   - 先设计归档任务与目标存储，再引入字段——字段是实现的投影，不是实现的承诺；
   - 以新 ADR 记录分层策略（保留周期、迁移目标、查询回源方式）。
3. 会话状态的权威存放仍为 PostgreSQL（唯一事实源）+ Redis 热副本（可回填），本决策不改变任何存储行为。

## 影响

- `OpenAgent.Contracts.Conversation.ConversationRecord` 公共表面缩小一个属性。全仓 grep 确认无其他编译期消费者（无测试、无前端、无 EF 映射）；Redis 中已缓存的会话 JSON 含该字段也不影响反序列化（System.Text.Json 默认忽略未知属性）。
- 后续若恢复归档能力，需新增 EF 映射与 migration，属正向变更，无兼容性负债。
- 本决策与会话架构重构系列（`docs/planning/session-architecture/`）中的"清理预留未实现语义"项对应。
