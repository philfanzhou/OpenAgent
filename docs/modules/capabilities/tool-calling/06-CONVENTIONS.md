# Conventions: 工具调用统一规则与执行循环

## 命名约定

- MCP 工具使用 `mcp_{server}_{tool}` 前缀，避免与本地 Skill 混淆
- RAG 工具使用固定系统名称 `search_knowledge_base`，避免和业务工具冲突
- 业务 Skill 名称应体现业务动作，而不是实现细节
- 以 `agent_tools-` 开头的工具名在执行前会自动去除前缀

## 执行路径约定

- 模型看到的是统一 ToolDefinition 集合
- Service 根据工具名称前缀判断来源并路由
- 不同来源的能力在主流程中保持一致的调用语义
- 工具结果以字符串形式回填到消息历史

## 参数约定

- 参数结构由 ParametersJsonSchema 定义
- 参数名稳定，不随实现变化
- 参数语义与工具描述一致
- 错误参数可以被识别和记录
- 无效 JSON 参数降级为 `{ "query": rawString }`

## 结果约定

- 工具结果为字符串类型
- 成功结果可直接用于后续对话推理
- 失败结果以 `"Error"` 前缀标识
- 原生 Function Calling：工具结果作为 `tool` 角色消息写回
- XML 降级：工具结果作为 `user` 角色消息写回

## 会话约定

- 工具调用前后 assistant 和 tool 消息关系必须清楚
- ToolCallId 用于关联请求与响应
- 工具调用消息纳入会话持久化

## 错误处理约定

- 错误可以被日志和追踪定位
- 不把底层实现细节直接暴露给最终用户
- AgentException 直接传播，其他异常包装为错误字符串
- 工具失败不会破坏整条执行链的状态一致性

## 循环控制约定

- 最大工具调用轮次由 AgentConfig.MaxTurns 控制，默认 5
- 达到上限时返回最后一条 assistant 消息或 "Max turns reached."
