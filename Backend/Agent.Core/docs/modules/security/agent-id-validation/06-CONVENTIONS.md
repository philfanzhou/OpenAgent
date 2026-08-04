# AgentIdValidation — 约定与规范

## 中间件命名

- `Name` 属性固定返回 `"AgentIdValidation"`（`nameof(AgentIdValidation)`），即类名本身

## 执行约定

- 验证在调用 `next` 之前执行（前置检查）
- 验证结果不影响管道执行（非阻断性检查）
- 同步和流式路径使用相同的验证逻辑（`ValidateAgentId`）
- 验证逻辑为同步操作

## 非阻断性约定

- AgentId 缺失不是致命错误
- 中间件仅提供可观测性（Debug 日志），不抛出异常
- 下游服务负责兜底处理（使用默认 AgentId）
- 这与 Auth 和 TenantValidation 的阻断性检查形成对比

## 日志约定

- AgentId 缺失记录 Debug 级别日志（非 Warning，因为这不是异常情况）
- 日志消息明确说明下游服务会处理
- AgentId 存在时不记录日志

## Pipeline 位置约定

- AgentIdValidation 应在 Auth 和 TenantValidation 之后
- 位置相对灵活，因为它是非阻断性的
- 建议在其他安全检查完成后执行

## 扩展约定

- 如需强制要求 AgentId（阻断性检查），应创建新的中间件而非修改 AgentIdValidation
- 如需 AgentId 格式验证，应扩展 `ValidateAgentId` 方法
- AgentId 的存在性检查和有效性检查应分离
