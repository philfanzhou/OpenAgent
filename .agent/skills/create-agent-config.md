# 创建 Agent 配置

## 用途
引导式工作流：在 OpenAgent 平台中创建新的 Agent 配置。

## 触发条件
- 用户要求"创建新 Agent"、"配置 Agent"、"添加智能体"
- 需要一个新的角色/场景的 Agent

## 输入参数
无（交互式收集需求）

---

## 工作流程

### 第一阶段：理解需求

向用户确认以下信息：
1. **Agent 名称和用途**：这个 Agent 做什么？（如"HR 助手"、"财务审批 Agent"）
2. **LLM 提供商和模型**：用什么模型？（如 `deepseek` + `deepseek-chat`）
3. **需要的技能（Skills）**：需要哪些内置技能？
4. **需要的 MCP 工具**：需要访问哪些数据库或 API？
5. **需要的 RAG 知识库**：需要检索哪些知识？
6. **权限和约束**：有什么特殊限制？（如最大轮次数）

### 第二阶段：参考现有配置

阅读以下文件了解配置 schema：

- Agent 配置模型：`Agent.Contracts/Configuration/AgentConfig.cs`
- 现有 Agent 示例：通过 Engine API 查看已注册的 Agent

- 可用 Skill 列表：通过 Engine API 查看 `skill:published:index`
- 可用 MCP 服务器：通过 Engine API 查看已注册 MCP 配置
- 可用 RAG 实例：通过 Engine API 查看 `rag:published:index`
- LLM 配置：通过 Engine API 查看 `llm:published:index`

### 第三阶段：创建 Agent 配置

通过 Engine API 创建 Agent 配置，JSON 格式参考：

```json
{
  "AgentId": "my-new-agent",
  "Name": "我的新 Agent",
  "Status": 2,
  "CurrentVersion": "1.0.0",
  "Config": {
    "Framework": "OpenAIDriver",
    "Llm": {
      "Provider": "deepseek",
      "ModelId": "deepseek-chat",
      "Temperature": 0.5
    },
    "Mcp": {
      "Servers": [
        {"Name": "hr-mcp", "Url": "http://localhost:8090/hr", "Type": "SSE"}
      ]
    },
    "Rag": {
      "Enabled": false,
      "EnabledRagInstanceIds": []
    },
    "Skills": {
      "EnabledSkills": ["calculator", "password-generator"]
    },
    "MaxTurns": 30
  }
}
```

**字段说明：**
- `AgentId`: 唯一标识符（kebab-case）
- `Framework`: `OpenAIDriver` / `MAF` / `SemanticKernel` / `Mock`
- `Llm.Provider`: 对应 `llm/` 目录下的 ProfileId
- `Llm.ModelId`: 提供商支持的模型名
- `Mcp.Servers`: MCP 服务器列表
- `Skills.EnabledSkills`: 已启用的 Skill ID 列表
- `MaxTurns`: 最大对话轮次

> ⚠️ 这些 JSON 会被 `Update-AgentConfigs` 在测试运行时重写。如需永久生效，需要在 `TestCode/scripts/lib/test-helpers.psm1` 的 `Update-AgentConfigs` 函数中注册此 Agent。

### 第四阶段：更新引擎注册表

在 Redis 的 `engine:registry:{engineId}` 中注册 Engine（如果通过引擎发现）。

### 第五阶段：验证

```powershell
# 启动服务
cd TestCode/scripts
./start-services.ps1

# 验证 Agent 已注册
curl http://localhost:5208/api/v1/agent/agents
# 应返回包含 my-new-agent 的列表

# 端到端测试
./test-e2e.ps1 --skip-llm
```

### 第六阶段：更新文档

- `AGENTS.md` — 如果新 Agent 代表了一种新模式
- `TestCode/README.md` — 如果新 Agent 有特殊的使用说明

---

## 参考文件
- Agent 配置模型：`Agent.Contracts/Configuration/AgentConfig.cs`
- 当前配置示例与 Engine API：`TestCode/docs/e2e-test-guide.md` 第 5 节
- Config 重写逻辑：`TestCode/scripts/lib/test-helpers.psm1` (`Update-AgentConfigs`)

## 验证方法
- Agent 通过 `/api/v1/agent/agents` 可发现
- Agent 能正常接收请求并调用配置的 Skill/MCP/RAG
- E2E 测试通过
