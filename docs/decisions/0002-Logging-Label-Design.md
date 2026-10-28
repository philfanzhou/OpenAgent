# ADR 0002：日志字段与索引标签

- 日期：2026-06-26
- 状态：已被当前可观测性设计取代
- 当前事实入口：[`../overview/Observability.md`](../overview/Observability.md)

## 历史决策

本 ADR 曾为应用直推 Loki 设计 `service`、`env` 等 label，并约束 `TenantId`、`UserId`、`AgentId`、`ConversationId`、`TraceId` 等高基数字段不得成为 Loki label。

高基数字段约束继续有效；Loki 专用 label 配置不再由应用维护。

## 当前规则

- `ServiceName`、`ServiceVersion`、`InstanceId` 是稳定日志属性。
- 请求关联信息保留为结构化事件属性或 trace attribute。
- 指标标签只使用有限、可枚举的低基数值。
- 日志存储的索引与 label 映射由部署侧采集配置负责。
- 应用不因某个具体日志后端而增加专用包或配置。
