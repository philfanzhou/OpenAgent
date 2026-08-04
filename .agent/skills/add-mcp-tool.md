# 添加 MCP 工具

## 用途
引导式工作流：向 OpenAgent 平台添加新的 MCP（Model Context Protocol）工具或 MCP 服务器。

## 触发条件
- 用户要求"添加 MCP 工具"、"接入新的数据源"、"增加 MCP 服务器"
- 需要让 Agent 访问新的外部数据或 API

## 输入参数
无（交互式收集需求）

---

## 工作流程

### 第一阶段：理解需求

向用户确认以下信息：
1. 这个 MCP 工具做什么？（一句话描述功能）
2. 是添加工具到已有 MCP 服务器，还是新建一个 MCP 服务器？
3. 数据源是什么？（SQLite 数据库 / HTTP API / 其他）
4. 需要哪些工具？（列出工具名称和功能）

### 第二阶段：学习现有模式

在动手写代码前，先阅读以下参考文件：

**MCP 协议与架构：**
- `docs/modules/capabilities/mcp/` — MCP 能力文档
- `docs/integrations/mcp-server/` — MCP 集成文档
- `Agent.Core/src/Core/` — IMcpClient 接口和 McpClient 实现

**测试 MCP 服务器（生产级参考）：**
- `TestCode/Agent.TestMCP/` — 完整的 MCP 服务器项目（3 个 SQLite 数据库）
- `TestCode/Agent.TestMCP/Program.cs` — MCP 入口（SSE + JSON-RPC）
- `TestCode/Agent.TestMCP/McpDatabaseService.cs` — 数据库查询 + 工具发现
- `TestCode/Agent.TestMCP/DatabaseInitializer.cs` — 数据初始化

**现有 MCP 工具命名规范：**
- `{db}_schema` — 查看表结构（如 `hr_schema`）
- `{db}_query` — 执行 SQL（如 `hr_query`）
- `{db}_search_{table}` — 条件搜索（如 `hr_search_employees`）

**测试指南：**
- `TestCode/scripts/integration/test-it-mcp-protocol.ps1` — MCP 协议测试脚本

### 第三阶段：实现

#### 场景 A：给已有 MCP 服务器加工具

1. 在对应 `Agent.TestMCP` 的数据库服务中添加新的 SQL 查询方法
2. 在 `McpDatabaseService` 的工具列表注册方法中添加条目
3. 遵循现有命名约定（`{db}_{operation}`）

#### 场景 B：新建 MCP 数据库

1. 创建 SQLite 数据库到 `TestCode/Data/`
2. 在 `TestCode/Agent.TestMCP/` 中：
   - 添加 `appsettings.json` 配置（数据库路径映射）
   - 添加 `DatabaseInitializer` 初始化逻辑（建表 + 种子数据）
3. 如果数据量大，新增独立的 MCP 项目（参考 `Agent.TestMCP` 结构）

#### 场景 C：新建 MCP 项目

1. 创建 `TestCode/Agent.TestMCP2/` 项目
2. 实现：
   - `Program.cs` — SSE + JSON-RPC 端点
   - `McpDatabaseService.cs` — 查询 + 工具发现
   - `appsettings.json` — 端口和数据库配置
3. 更新 `TestEnv.sln` 加入新项目

#### 所有场景都需要：

**日志 scope 限制：**

- `ToolCallLog` scope 只记录工具名、类型、服务器名/地址和 binding 名；AgentId、TraceId、
  ConversationId 由 Core 最外层 tracing scope 提供，不重复写入。
- 参数只记录 key、类型、长度和不可逆摘要，不记录 API Key、token、完整参数值或完整结果。
- 流式工具调用只记录开始、结果摘要和异常等必要事件，不按 chunk 写日志。

**更新 Agent 配置：**
通过 Engine API 在对应 Agent 配置的 `Mcp.Servers` 中添加：
```json
{"Name": "new-mcp", "Url": "http://localhost:<port>/<完整-endpoint>", "Type": "Http"}
```

> ⚠️ 注意：Agent 配置通过 Engine API 管理，不再使用本地 JSON 文件。
> `Http` 类型的 `Url` 是完整 Streamable HTTP endpoint，客户端不会自动追加 `/mcp`。
> MCP 注册表条目必须显式加入 Agent 的 `Mcp.Servers` 并发布 Agent 配置后才会生效。

### 第四阶段：验证

1. 构建 `TestEnv.sln`：
   ```powershell
   dotnet build TestCode/TestEnv.sln
   ```
2. 启动服务：
   ```powershell
   cd TestCode/scripts
   ./start-services.ps1
   ```
3. 用 MCP 协议测试脚本验证：
   ```powershell
   cd TestCode/scripts/integration
   ./test-it-mcp-protocol.ps1
   ```

### 第五阶段：更新文档

确认以下文档是否需要更新：
- `docs/modules/capabilities/mcp/` — 如果新增能力类型
- `AGENTS.md` — 如果约定或配置方式变化

---

## 参考文件
- MCP 协议实现：`TestCode/Agent.TestMCP/Program.cs`
- 数据库服务：`TestCode/Agent.TestMCP/McpDatabaseService.cs`
- Agent 配置示例：通过 Engine API 查看（旧模板已移除）

## 验证方法
- 新工具在 MCP 协议层可被发现和调用
- 集成测试通过
- Agent 能通过 `IAgentEngine` 正常调用新工具
