# Pipeline — 约定与规范 (CONVENTIONS)

## 命名约定

- 中间件类名使用名词（如 AuditLogging, Tracing, Auth）
- 中间件 Name 属性返回类名（`nameof(ClassName)`）
- 委托类型：`AgentPipelineDelegate`、`AgentStreamPipelineDelegate`

## 日志和安全要求

- Pipeline 入口记录 Query、TraceId、UserId
- Pipeline 出口记录 Success、ErrorCode
- 中间件异常不吞没，向上传播

## 错误消息格式约定

| 场景 | 消息文本 |
|------|----------|
| AgentException | 继承原始异常的 ErrorCode 和 Message |
| 非 AgentException | ErrorCode=InternalError, Message=ex.Message |
