# Feature: RAG 检索增强

## 用户故事

作为长对话场景的用户，我希望 Agent 能检索内部知识库中的相关信息来回答问题，以便获得准确的、基于事实的回答。

## 概述

RAG（Retrieval-Augmented Generation）在 Agent.Core 中作为模型可调用的工具参与执行，而不是在所有请求开始前强制预检索。RAG 是否被调用由模型和工具链路共同决定。

## 核心能力

| 能力 | 说明 |
|------|------|
| 文档索引 | 将内容索引到外部 RAG 系统 |
| 语义检索 | 从外部 RAG 系统检索相关文档 |
| 多实例支持 | 同时配置和使用多个 RAG 实例 |
| 适配器扩展 | 通过 IRagAdapter 支持不同 RAG 产品 |
| 权限过滤 | 基于用户上下文的 ACL 过滤 |

## 已实现适配器

| 适配器 | 类型标识 | 说明 |
|--------|---------|------|
| QdrantAdapter | `qdrant` | Qdrant 向量数据库 |
| RagFlowAdapter | `ragflow` | RagFlow 知识库平台 |

## 与工具体系的关系

RAG 通过 RagSearchTool（实现 ISkillExecutor）接入工具调用体系：
- 工具名称：`search_knowledge_base`
- 由 Service 在工具收集阶段根据 `config.Rag.Enabled` 决定是否纳入

## 当前状态

**已实现** — 多实例检索、适配器扩展、权限过滤均已落地。

## 当前限制

- Qdrant 索引请求中向量字段使用空数组 `float[0]`，实际使用时需要调用嵌入模型
- RagFlowAdapter 不支持索引操作（BuildIndexRequest 返回 null）
- 适配器响应解析使用同步 `.GetAwaiter().GetResult()`
- 无检索结果缓存

## 相关文档

- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
