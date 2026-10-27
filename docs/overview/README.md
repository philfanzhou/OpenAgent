# 总览文档

| 文档 | 用途 |
|---|---|
| [开发指南](DevelopmentGuide.md) | 模块入口、分工、扩展接口与验证 |
| [系统上下文](SystemContext.md) | 当前服务、依赖、功能范围与 Workflow 状态 |
| [运行时设计](Design.md) | 分层、ACP 执行与生命周期 |
| [历史分析报告](../archive/README.md) | 执行链路分析与执行域精简盘点（快照基线，已归档） |
| [关键流程](KeyFlows.md) | 跨服务时序与调用链 |
| [API](API.md) | HTTP/SSE、认证、错误与接口约定 |
| [集成矩阵](Integration.md) | 外部系统与接口边界 |
| [数据所有权](DataOwnership.md) | 数据主责、引用边界与双写禁区 |
| [可观测性](Observability.md) | 日志、Trace、Metrics 与健康检查 |

阅读路径：系统上下文 → 运行时设计 → 关键流程。开发时从开发指南进入所属功能域；分派和跟踪工作统一见 [任务清单](../planning/TODO.md)。
