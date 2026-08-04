# Testing: 工具调用统一规则与执行循环

## 测试策略

工具调用的测试围绕工具收集、执行路由、调用循环三个核心链路展开。

## 单元测试

### 工具收集

| 测试场景 | 验证点 |
|----------|--------|
| Skill 描述符转为 ToolDefinition | Name/Description/Schema 正确传递 |
| RAG 工具添加 | config.Rag.Enabled=true 且 RagSearchTool 存在时添加 |
| RAG 工具不添加 | config.Rag.Enabled=false 时不添加 |
| MCP 工具加载 | 别名生成正确，绑定注册正确 |
| MCP 服务器加载失败 | Warning 日志，跳过该服务器 |

### 执行路由

| 测试场景 | 醴证点 |
|----------|--------|
| search_knowledge_base | 路由到 RagSearchTool |
| mcp_ 前缀工具 | 路由到 ExecuteMcpToolAsync |
| 普通工具 | 路由到 SkillProvider.ExecuteAsync |
| agent_tools- 前缀 | 去除前缀后路由 |
| AgentException 直接抛出 | 不包装 |
| 其他异常 | 返回 "Error executing tool: ..." 字符串 |

### 参数解析

| 测试场景 | 验证点 |
|----------|--------|
| 空字符串 | 返回空 Dictionary |
| 有效 JSON | 反序列化为 Dictionary |
| 无效 JSON | 原始字符串作为 query 键值 |

### MCP 别名生成

| 测试场景 | 验证点 |
|----------|--------|
| 正常名称 | mcp_{server}_{tool} 格式 |
| 特殊字符归一化 | 非字母数字替换为下划线，转小写 |
| 重名处理 | 追加 _2, _3 后缀 |

### XML 降级

| 测试场景 | 验证点 |
|----------|--------|
| 包含 `<tool_use>` 标签 | 正确提取 name 和 arguments |
| 不包含 `<tool_use>` 标签 | 返回 false |
| 缺少 name/arguments 标签 | 返回空字符串 |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整工具调用循环 | 模型返回 ToolCall → 执行 → 结果回填 → 继续推理 |
| 多轮工具调用 | turn 计数正确，达到 maxTurns 时终止 |
| 流式工具调用 | chunk 合并正确，工具结果正确回填 |
| 取消时写回 | OperationCanceledException → 状态 Cancelled |
| 失败时写回 | 异常 → 状态 Failed |

## 验收口径

- [ ] 三种工具来源均能正确收集为 ToolDefinition
- [ ] 执行路由按名称前缀正确分发
- [ ] 原生 Function Calling 和 XML 降级均能工作
- [ ] maxTurns 限制生效
- [ ] 工具失败不破坏执行链状态
