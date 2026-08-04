# Agent Review Notes

## 1. 本轮任务范围

为 Agent.Core 项目整理/优化 docs 目录，基于实际代码补齐、修正、整理文档。覆盖 overview/、modules/、Integration/、database/ 全部目录。

## 2. 已完成的文档调整

### overview/ (8 个文件)
- **README.md** — 重写为总览索引，含文档清单表格和阅读建议
- **SystemContext.md** — 重写，明确 Agent.Core 为类库而非独立服务，更新上下游架构图
- **Integration.md** — 重写为集成矩阵，补充失败语义总结
- **KeyFlows.md** — 重绘 3 个核心流程时序图（非流式、流式、双写）
- **DataOwnership.md** — 补充双写规则和禁止事项
- **Requirements.md** — 重构为 R-01~R-10 编号体系，链接到 modules/
- **Design.md** — 补充项目组成表、分层架构图、核心抽象表、中间件注册代码、DI 入口
- **DotNetCodingPolicy.md** — 新建，从 99-engineering/ 迁移并基于代码补充
  > **注意**：该文件已合并到 `.agent/rules/coding-conventions.md`。

### modules/ (5 个域，15 个功能点)
- **execution/** — pipeline、service、streaming、errors 各 6 件套（24 文件）
- **security/** — auth、tenant、agent-id-validation、permission 各 6 件套（24 文件）
- **engine/** — 统一 SDK Runtime 文档与测试替身文档
- **capabilities/** — skill、tool-calling、mcp、rag 各 6 件套（24 文件）
- **conversation/** — store、context-compression 各 6 件套（12 文件）

### Integration/ (6 个集成点)
- llm-provider、matrix、mcp-server、rag-service、redis、sqlserver 各 6 件套（36 文件）

### database/ (3 个文件)
- **README.md** — 修正表清单，补充双写架构说明
- **tables/ConversationRecords.md** — 补全字段、索引、MessagesJson 格式、并发控制
- **tables/ConversationMessages.md** — 从"独立表"改为"JSON 嵌入"说明

### 根目录 (2 个文件)
- **README.md** — 重写为导航入口
- **AgentReviewNotes.md** — 新建

## 3. 待人工审核事项

> **2026-06-12 更新**：以下 REV-001 ~ REV-003 已全部完成清理。旧文件内容已验证合并到新文件后删除。详见下方各条目。

### 3.1 旧命名文件残留 ✅ 已解决
- **编号**：REV-001
- **问题描述**：engine/ 下的 semantic-kernel、maf、openai-driver、mock 子目录中存在旧命名文件（02-ARCHITECTURE.md、03-API.md、04-DATA-MODEL.md、05-EXAMPLES.md），与新的标准命名文件（02-SPEC.md、03-DESIGN.md、04-TASKS.md、05-TESTS.md）并存
- **已查阅的代码/配置/测试证据**：确认新文件已包含完整内容，旧文件为冗余
- **处理结果**：旧文件中的架构图已合并到 02-SPEC.md，数据模型已合并到 03-DESIGN.md，代码示例已合并到 06-CONVENTIONS.md。旧文件已删除。

### 3.2 旧文档目录残留 ✅ 已解决
- **编号**：REV-002
- **问题描述**：docs/ 根目录下仍存在 00-overview/、01-execution/、02-capabilities/、99-engineering/ 旧目录
- **已查阅的代码/配置/测试证据**：docs/README.md 已将这些目录标记为"旧文档归档"
- **处理结果**：4 个旧目录及其中 13 个文件已全部删除。内容已迁移至 overview/ 和 modules/。

### 3.3 security 模块双命名文件 ✅ 已解决
- **编号**：REV-003
- **问题描述**：security/ 下的 auth、tenant、agent-id-validation、permission 子目录中同时存在旧命名（01-AUTH.md、02-INTERFACES.md 等）和新命名（01-FEATURE.md、02-SPEC.md 等）文件
- **已查阅的代码/配置/测试证据**：新旧文件逐字节完全一致
- **处理结果**：20 个旧命名文件已删除。

### 3.4 capabilities 模块错误内容合并 ✅ 已完成
- **编号**：REV-004（新增）
- **问题描述**：capabilities/ 下的 mcp、rag、skill、tool-calling 目录中旧 05-ERRORS.md 包含错误码表、异常类型、排障指南，新 05-TESTING.md 为测试内容，两者主题不同
- **处理结果**：错误码、异常类型、错误处理表、排障指南已合并到各模块的 02-ARCHITECTURE.md。旧文件已删除。

## 4. 证据不足但已落盘的内容

- **ConversationStatus 枚举值**（0=Running, 1=Completed, 2=Failed, 3=Cancelled）为 [推断]，基于枚举声明顺序推断，未在代码中找到显式赋值
- **Redis key TTL 默认值**基于 ConversationStoreOptions 配置推断，未确认 RedisTtlMinutes 的默认配置值
- **context-compression** 文档中标注"规划中"，代码中仅发现 `TakeLast(MaxHistoryMessages)` 简单截断，无 IContextCompressor 或相关实现
- **McpExceptions** 当前只保留代码中实际存在的 `ConnectionException`；协议错误使用官方 SDK 异常类型。

## 5. 风险与后续建议

- **旧文件清理**：✅ 已完成。56 个旧命名文件 + 4 个旧目录已全部清理。所有模块现统一使用标准 6 件套命名（01-FEATURE ~ 06-CONVENTIONS）。
- **Capabilities 模块**：✅ 已完成。错误码和排障内容已合并到各模块的 02-ARCHITECTURE.md。
- **Engine 模块**：✅ 已完成。架构图、代码示例等独特内容已合并到对应的 02-SPEC.md、03-DESIGN.md、06-CONVENTIONS.md。
- **测试覆盖**：多个模块的 05-TESTS.md 中列出了"遗漏的测试场景"，建议后续补充单元测试

## 6. 2026-07-10 SRP 重构目录收口

- `Service`、Pipeline、resolver、工具与持久化实现已迁移到 `src/Core/Execution/`。
- 会话存储、锁、仓储与压缩实现已迁移到 `src/Core/Conversation/`。
- MCP、RAG、Skill 实现已迁移到 `src/Core/Capabilities/`。
- 公开类型 namespace 保持不变；internal 类型按职责迁移。相关文档中的源文件路径已同步更新。

## 7. 2026-07-15 master 合并与 MCP SDK 收口

- 保留 `master` 的官方 `ModelContextProtocol.Core` 1.4.1 与 `McpServerType.Http` 能力。
- 将官方 SDK adapter 重新拆分为 `McpConnection`、`McpTransportFactory`、`McpToolCatalog`、`McpToolInvoker`、`McpResourceReader` 与 `McpSessionState`。
- 删除由 SDK 接管的手写 JSON-RPC、SSE parser 与重连组件，以及未使用的 `ConnectionPool`。
- MCP 文档已按源码更新为 legacy SSE 与 Streamable HTTP 双 transport，`Stdio` 仍未实现。

## 8. 2026-07-22 MAF 单内核收口

- Microsoft Agent Framework 已成为唯一生产 Agent 引擎，Mock 仅用于测试。
- 删除 `src/OpenAIDriver/`、`src/SemanticKernel/` 及两个模块的 12 份失效文档；其仍有价值的 Provider、消息、工具和兼容说明已合并到 `modules/engine/maf/` 与 `Integration/llm-provider/`。
- 删除自研模型/工具循环辅助组件，MAF `FunctionInvokingChatClient` 接管函数循环；平台保留权限、工具执行、会话和遥测边界。
- 更新 engine 索引、Core/Engine overview、integration matrix 和 LLM Provider 六件套，清除已删除项目的源码链接。
- `SemanticKernel`、`LangChain`、`OpenAIDriver` 名称只保留为配置兼容别名，运行时全部归一为 MAF。
