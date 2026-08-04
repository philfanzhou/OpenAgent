# Testing: RAG 检索增强

## 测试策略

RAG 检索增强的测试围绕配置解析、多实例检索、适配器行为和降级策略展开。

## 单元测试

### RagService 配置解析

| 测试场景 | 验证点 |
|----------|--------|
| overrideConfig 非空 | 直接使用 overrideConfig |
| overrideConfig 为空 | 根据 agentId 获取 AgentConfig.Rag |
| Instances 非空 | 使用内联实例配置 |
| Instances 为空 + EnabledRagInstanceIds 非空 | 从 IRagRegistry 筛选 |
| Enabled=false 过滤 | 不包含禁用实例 |
| ACL 过滤 | 无权限实例被排除 |

### RagService 检索

| 测试场景 | 验证点 |
|----------|--------|
| 无可用配置 | 返回空列表 |
| 单实例检索 | 正确调用适配器并返回结果 |
| 多实例检索 | 合并结果，按 RelevanceScore 降序 |
| 单实例异常 | 不中断其他实例，Error 日志 |
| ApiEndpoint 为空 | 跳过该实例，Warning 日志 |
| 适配器未找到 | 返回空列表，Error 日志 |

### RagService 索引

| 测试场景 | 验证点 |
|----------|--------|
| 无可用配置 | 跳过索引，Debug 日志 |
| 指定 ragInstanceId | 仅索引目标实例 |
| 适配器不支持索引 | 跳过索引，Debug 日志 |
| ApiEndpoint 为空 | 跳过索引，Warning 日志 |
| HTTP 请求失败 | Error 日志，不抛出 |

### RagSearchTool

| 测试场景 | 验证点 |
|----------|--------|
| 正常检索 | 返回编号列表格式 |
| query 为空 | 返回 "Error: Query parameter is required" |
| 检索异常 | 返回 "Error searching knowledge base: ..." |
| 无结果 | 返回 "No relevant information found in knowledge base." |
| limit 参数 | 默认 3，可配置 1-10 |

### QdrantAdapter

| 测试场景 | 验证点 |
|----------|--------|
| CanHandle Type 匹配 | config.Type="qdrant" 时返回 true |
| CanHandle Endpoint 匹配 | ApiEndpoint 包含 "qdrant" 时返回 true |
| BuildSearchRequest | 正确构建 POST 请求，含 api-key Header |
| ParseSearchResponse | 正确解析 Result 数组 |
| BuildIndexRequest | 返回含空向量的索引请求 |

### RagFlowAdapter

| 测试场景 | 验证点 |
|----------|--------|
| CanHandle Type 匹配 | config.Type="ragflow" 时返回 true |
| CanHandle Endpoint 匹配 | ApiEndpoint 包含 "ragflow" 时返回 true |
| BuildSearchRequest | 正确构建 POST 请求，含 Bearer Header |
| ParseSearchResponse | 正确解析 Data.Chunks |
| BuildIndexRequest | 返回 null（不支持索引） |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整检索周期 | 配置解析 → 适配器选择 → HTTP 请求 → 结果合并 |
| 多实例并行检索 | 所有实例结果合并排序 |
| 工具调用集成 | search_knowledge_base 工具被 Service 正确注册和路由 |

## 验收口径

- [ ] RAG 工具在 config.Rag.Enabled=true 时出现在工具集合
- [ ] 多实例检索结果正确合并排序
- [ ] 单实例失败不阻断其他实例
- [ ] ACL 过滤正确执行
- [ ] 适配器 CanHandle 匹配逻辑正确
