# 架构决策（ADR）

| 文档 | 决策 | 状态 |
|---|---|---|
| [0003 运行配置解析](0003-Agent-Runtime-Profile-Resolution.md) | 集中解析 Agent 运行配置与请求边界 | 已决策，第一版已实现 |
| [0004 会话归档语义](0004-Conversation-Archive-Semantics.md) | 移除 ArchivedAt 预留字段，归档语义延后设计 | 已决策，已实现 |
| [0004 API 风格与错误契约](0004-Unified-API-Style-Minimal-Api-ProblemDetails.md) | Minimal API、TypedResults 与 ProblemDetails | 已决策，第一版已实现 |
| [0001 日志框架](0001-Logging-Framework-Serilog-Loki.md) | 历史框架与存储后端选型 | 已取代，当前事实见 [可观测性](../overview/Observability.md) |
| [0002 日志标签](0002-Logging-Label-Design.md) | 历史字段与索引标签设计 | 已取代，当前事实见 [可观测性](../overview/Observability.md) |

已整合的 Contracts/Hosting 旧文档见 [历史归档](../archive/README.md)。待实施工作统一见 [任务清单](../planning/TODO.md)。
