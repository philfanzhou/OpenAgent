# Agent Provider 集成

Router 通过 `IAgentProvider` 统一调用自有 Engine 和第三方 Agent 服务。Provider 负责自己的 Agent 列表协议、意图 Agent 调用、目标地址解析和协议相关的请求配置；Router 负责意图识别编排与统一转发。

## 请求流程

当请求已经包含 `agentId` 或 `conversationId` 时，Router 不执行意图识别，也不查询或校验会话。否则 Router 按以下顺序处理：

1. 调用所有 Provider 的 `GetAgentsAsync` 聚合 Agent 列表。
2. 如果注册了 `IAgentAccessControl`，将候选列表交给权限实现过滤。
3. 使用 `IntentRecognition:ProviderId` 找到意图识别 Provider，并把候选 Agent 与用户消息交给配置的 `AgentId` 完成意图识别。
4. 校验模型返回的 Agent ID，将其写入 `X-Agent-Id`。
5. 调用候选 Agent 所属 Provider 解析目标，由 Router 的统一转发器转发原始请求。

`HttpContext` 只保留在 Router 的 HTTP/YARP 边界，不再沿 Provider 调用链传递。Agent 列表查询和意图识别使用 Provider 自己的服务身份；权限扩展点只接收 `IAgentUserContext` 与候选列表。真实转发仍保留原始请求体、Header、附件和取消信号，Provider 可以通过 `ConfigureRequestAsync` 调整最终的 `HttpRequestMessage`。Router 不定义通用聊天请求 DTO，也不要求不同服务使用相同的网络协议。

## 配置

自有 Engine 也作为 Provider 持久化在配置中：

```json
{
  "RouterSettings": {
    "IntentRecognition": {
      "Enabled": true,
      "ProviderId": "self-engine",
      "AgentId": "intent-router",
      "FallbackAgentId": "default",
      "MinimumConfidence": 0.5,
      "TimeoutMs": 5000
    },
    "AgentProviders": {
      "DefaultProviderId": "self-engine",
      "Providers": [
        {
          "Id": "self-engine",
          "Type": "OpenAgentEngine",
          "Settings": {
            "AgentListPath": "/api/v1/agent/agents",
            "ChatPath": "/api/v1/agent/chat",
            "ServiceHeaders": {
              "Authorization": "use-a-secret-provider",
              "X-Tenant-Id": "intent-service"
            }
          }
        },
        {
          "Id": "partner-a",
          "Type": "PartnerA",
          "Settings": {
            "BaseUrl": "https://partner.example",
            "ApiKey": "use-a-secret-provider",
            "Custom": {
              "Region": "east"
            }
          }
        }
      ]
    }
  }
}
```

公共配置只定义 `Id`、`Type` 和不透明的 `Settings`。Router 不解析 `Settings`，每个 Provider Factory 可以自行读取任意嵌套参数。敏感凭据应由环境变量或密钥配置源注入。

`DefaultProviderId` 用于已有 `agentId`、已有 `conversationId` 以及 fallback Agent 没有候选来源时的兼容转发，它不是意图识别失败时的 Provider fallback。配置中没有 `FallbackProviderId` 字段。`RouterSettings:Routing`（`EngineEndpoint`/`WorkflowEndpoint`）是 `InMemoryRouteTable` 使用的静态路由回退，不属于某个 Provider 的 `Settings`。

## 实现 Provider

第三方接入需要实现 `IAgentProviderFactory` 和 `IAgentProvider`，并注册 Factory：

```csharp
services.AddSingleton<IAgentProviderFactory, PartnerAgentProviderFactory>();
```

Factory 的 `Type` 必须与配置一致。`Create` 会收到 Provider ID 和该 Provider 的 `Settings` 配置节。Provider 的四个操作具有不同职责：

- `GetAgentsAsync`：使用 Provider 服务身份读取 Agent 列表并转换为 `AgentSummary`。
- `RecognizeIntentAsync`：接收意图 Agent ID、候选 Agent 与用户消息，返回标准化的 `IntentRecognitionResult`。
- `ResolveForwardingAsync`：根据 action、租户 ID 和会话 ID 解析目标地址。
- `ConfigureRequestAsync`：在统一转发前调整目标 `HttpRequestMessage`，处理服务方认证或自定义协议。

Provider 内可以注入自己的 HTTP 客户端、SDK、RouteTable 或认证服务。内置 `OpenAgentEngineProvider` 在 Provider 内调用 `IRouteTable`，因此没有把 endpoint 放进公共配置。Agent 列表与意图识别均不继承用户请求身份；调用自有 Engine 所需的服务身份可以通过 Provider 的 `ServiceHeaders` 配置。

`IAgentAccessControl` 是可选扩展点。没有实现时候选列表直接进入意图识别；当前 Router Host 注册了基于 `IAgentVisibilityService` 的实现，并且只在候选聚合完成后过滤一次。
