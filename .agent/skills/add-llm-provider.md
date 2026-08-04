# 添加 LLM 提供商

## 用途
引导式工作流：向 OpenAgent 平台添加新 LLM 提供商的支持。

## 触发条件
- 用户要求"接入新的 LLM"、"添加模型提供商"、"支持新平台"
- 需要切换或添加 AI 模型服务

## 输入参数
无（交互式收集需求）

---

## 工作流程

### 第一阶段：理解需求

向用户确认以下信息：
1. 提供商名称和 API 端点 URL？
2. 是 **OpenAI 兼容** API 还是需要**自定义适配器**？
3. 认证方式（API Key / OAuth / 其他）？
4. 需要支持哪些模型？

### 第二阶段：学习现有模式

在动手前，先阅读：

**提供商集成架构：**
- `docs/integrations/llm-provider/` — LLM 提供商集成文档
- `Agent.Core/src/OpenAIDriver/` — OpenAI 兼容驱动（处理所有标准 API）
- `Agent.Contracts/Configuration/AgentConfig.cs` — LlmProviderProfile、ApiFormat 定义

**现有提供商配置：**
- LLM Profile 通过 Engine API 创建（不再使用本地 JSON 文件）
- API Key 配置：`TestCode/.env.example` — 环境变量模板

### 第三阶段：实现

#### 场景 A：OpenAI 兼容 API（95% 的情况）

大多数 LLM 服务都兼容 OpenAI API 格式，**不需要修改任何 C# 代码**。只需：

**步骤 1: 添加提供商配置**

通过 Engine API 创建 LLM Profile，JSON 格式参考：
```json
{
  "ProfileId": "<provider-id>",
  "Name": "<显示名称>",
  "Endpoint": "https://api.<provider>.com/v1",
  "ApiFormat": "OpenAI",
  "Models": ["model-name-1", "model-name-2"],
  "DefaultModel": "model-name-1"
}
```

**步骤 2: 添加 API Key 支持**

在 `TestCode/.env.example` 中添加：
```
<PROVIDER_ID>_API_KEY=your-key-here
```

规则：Provider ID 转为大写，连字符变下划线。如 `my-provider` → `MY_PROVIDER_API_KEY`。

**步骤 3: 验证 Key 解析**

`TestCode/scripts/lib/test-helpers.psm1` 中的 `Get-AllLlmApiKeys` 会自动按 `{PROVIDER_ID}_API_KEY` 格式发现新 Key，一般无需修改。

#### 场景 B：需要自定义适配器（非 OpenAI 兼容）

1. 在 `Agent.Core/src/<ProviderName>/` 创建新引擎项目
2. 实现 `IAgentEngine` 接口（ChatCompletion + StreamChatCompletion）
3. 如提供商有独特配置需求，实现 `ILlmRegistry` 适配器
4. 在 `ServiceExtensions.cs` 中注册
5. 添加文档到 `docs/modules/engine/<provider-name>/`

### 第四阶段：验证

```powershell
# 1. 配置 API Key
cp TestCode/.env.example TestCode/.env
# 编辑 .env 填入新提供商的 Key

# 2. 用新提供商运行 E2E 测试
cd TestCode/scripts
./test-e2e.ps1 -Provider <new-provider-id>
```

### 第五阶段：更新文档

- 根 `README.md` 与 `docs/integrations/llm-provider/` — 仅在新增适配器或约定时更新
- `TestCode/README.md` — 更新提供商表格
- `docs/integrations/llm-provider/` — 如果是自定义适配器

---

## 参考文件
- Engine API 与 LLM Profile 字段：`TestCode/docs/e2e-test-guide.md` 第 5、6 节
- API Key 环境变量模板：`TestCode/.env.example`
- OpenAI 驱动实现：`Agent.Core/src/OpenAIDriver/`
- LLM 配置模型：`Agent.Contracts/Configuration/AgentConfig.cs`

在保存或启动前校验 Endpoint、ApiKey、Provider 和 ModelId；手工模式下 Endpoint 与 ApiKey
必须同时存在，注册表模式不得残留手工凭据字段。

- 执行初始化阶段必须完成 LLM 配置校验，不能等到首个模型请求才报错。
- 使用 registry 模式时，无法解析 Provider/Profile 必须抛出包含 provider 标识的
  `InvalidOperationException`，不得静默回退到空 endpoint。
- RedisTool 保存到 Agent 配置的 registry 模式只保留 `provider`、`format`、`modelId` 和
  推理参数；`endpoint`、`apiKey` 只属于手工模式。
- 热更新 JSON 必须保留 `JsonStringEnumConverter`，确保字符串枚举配置兼容。

## 验证方法
- 新提供商出现在 `Get-AvailableProviders` 输出中
- E2E 测试通过（Agent 能正常调用新提供商的 API）
