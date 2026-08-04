# 新增平台组件

向 OpenAgent 平台添加新组件的通用工作流。根据组件类型选择对应分支。

## 触发条件

- 用户要求"添加 LLM 提供商"、"接入新模型" → [LLM 提供商](#llm-提供商)
- 用户要求"添加 MCP 工具"、"接入新数据源" → [MCP 工具](#mcp-工具)
- 用户要求"添加技能"、"新建 Skill" → [Agent 技能](#agent-技能)
- 用户要求"添加新引擎"、"接入新框架" → [Agent 引擎](#agent-引擎)

---

## 通用流程

所有组件新增共享以下阶段：

1. **理解需求**：确认功能边界、输入输出、依赖关系
2. **学习模式**：阅读现有实现和参考文档
3. **实现**：遵循依赖方向 `Contracts ← Core ← Engine ← Host`
4. **验证**：`dotnet build` + `dotnet test`
5. **文档**：更新 `docs/` 对应模块 + `AGENTS.md`（如约定变化）

---

## LLM 提供商

### 场景判断

| 情况 | 处理方式 |
|------|---------|
| OpenAI 兼容 API（95%） | 仅配置，不改 C# 代码 |
| 非兼容协议 | 参考 [Agent 引擎](#agent-引擎) |

### OpenAI 兼容接入

1. 通过 Engine API 创建 LLM Profile：
   ```json
   {
     "ProfileId": "<provider-id>",
     "Name": "<显示名称>",
     "Endpoint": "https://api.<provider>.com/v1",
     "ApiFormat": "OpenAI",
     "Models": ["model-name-1"],
     "DefaultModel": "model-name-1"
   }
   ```
2. 在 `TestCode/.env.example` 添加 API Key 变量：`{PROVIDER_ID}_API_KEY`
3. 更新 `docs/integrations/llm-provider/`

### 参考

- 集成文档：`docs/integrations/llm-provider/`
- 配置模型：`Agent.Contracts/Configuration/AgentConfig.cs`
- OpenAI 驱动：`Agent.Core/src/OpenAIDriver/`

---

## MCP 工具

### 场景判断

| 情况 | 处理方式 |
|------|---------|
| 给已有 MCP 服务器加工具 | 在 `TestCode/Agent.TestMCP/` 添加 SQL 查询 + 工具注册 |
| 新建 MCP 数据库 | 创建 SQLite + 按命名约定注册工具 |

### 命名约定

- `{db}_schema` — 查看表结构
- `{db}_query` — 执行 SQL
- `{db}_search_{table}` — 条件搜索

### 参考

- MCP 能力文档：`docs/modules/capabilities/mcp/`
- MCP 集成文档：`docs/integrations/mcp-server/`
- 测试 MCP 服务器：`TestCode/Agent.TestMCP/`
- 接口：`Agent.Core/src/Core/`（IMcpClient）

---

## Agent 技能

### 场景判断

| 类型 | 位置 | 特点 |
|------|------|------|
| 简单技能 | `TestCode/Agent.TestSkillService/Skills/Simple/` | 纯逻辑，不调外部 |
| 业务技能 | `TestCode/Agent.TestSkillService/Skills/Business/` | 调用 MCP 工具组合 |

### 实现步骤

1. 继承 `SkillBase`，实现 `ExecuteAsync`
2. 在 `SkillRegistry.cs` 注册
3. 通过 Engine API 创建 Skill 定义
4. 在 Agent 配置的 `Skills.EnabledSkills` 中绑定

### 参考

- 技能能力文档：`docs/modules/capabilities/skill/`
- 基类：`TestCode/Agent.TestSkillService/SkillBase.cs`
- 注册：`TestCode/Agent.TestSkillService/SkillRegistry.cs`
- 简单模板：`TestCode/Agent.TestSkillService/Skills/Simple/CalculatorSkill.cs`
- 业务模板：`TestCode/Agent.TestSkillService/Skills/Business/Employee360Skill.cs`

---

## Agent 引擎

### 实现步骤

1. 在 `Agent.Core/src/<EngineName>/` 创建独立项目
2. 实现 `IAgentEngine`（ChatCompletion + StreamChatCompletion）
3. 如需新框架值，在 `EngineFrameworkType` 添加兼容值
4. 通过 `IAgentEngineFactory` 注册（使用 `TryAdd*` 保证幂等）
5. 使用 `[LoggerMessage]` 源生成日志，分配稳定 EventId
6. 先写引擎单测，再补 Factory/DI 测试

### 验证

```bash
dotnet test Backend/OpenAgent/Agent.Core/OpenAgent.Core.sln
dotnet test TestCode/TestEnv.sln
```

### 参考

- 引擎文档：`docs/modules/engine/`
- 框架类型：`Agent.Contracts/`（EngineFrameworkType）
