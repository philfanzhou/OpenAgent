# Agent Provider 集成

Router 通过 `IAgentProvider` 统一调用自有 Engine 和第三方 Agent 服务。Provider 负责目录协议、会话归属探测、意图调用、目标解析和请求配置；Router 负责公开目录、授权、Provider 映射、会话亲和与统一转发。

## 公开 Agent Catalog

`GET /api/v1/agent/agents`（兼容别名：`GET /api/v1/agents`）会读取所有 Provider 的目录，并依次应用已注册的 `IAgentAccessControl`。响应仍为按 `agentId` 排序的 `AgentSummary[]`，不会包含 `providerId`、内部地址或 Provider 配置。

公开 `agentId` 保留 Provider 发布的稳定 ID，Router 只在内部维护其 Provider 映射。同一用户可见范围内若两个 Provider 发布相同 `agentId`，目录和显式选择均返回 `409 agent_id_conflict`，不会用 Provider 前缀改写 ID 或按注册顺序静默选取。

显式 `agentId` 必须先经过完整目录和授权过滤，再定位 Provider。不存在和未授权统一返回 `404 agent_not_found`，避免利用显式选择探测不可见 Agent。任何 Provider 的目录不可用时返回 `503 agent_provider_unavailable`，避免在不完整快照上产生不稳定映射。

## 会话归属与亲和

亲和记录以租户和 `conversationId` 的摘要为键，值只保存内部 `providerId` 与 `Pending`/`Confirmed` 状态；配置 Redis 时由分布式缓存持久化，未配置时使用进程内分布式缓存实现。

1. 新会话完成显式或自动选择后写入 `Pending` 亲和，重试仍固定到同一 Provider。
2. 续聊先读取亲和，再通过 Provider contract 验证归属；首次验证成功后转为 `Confirmed`。
3. 旧会话没有亲和时逐一探测所有 Provider；唯一命中会回填 `Confirmed`，实现无停机迁移。
4. 已确认会话在原 Provider 不存在时重新探测其他 Provider；唯一命中会更新归属。
5. 已绑定 Provider 不可用、迁移探测不完整或 Provider 已移除时不回退到默认 Provider，避免拆分会话历史。

亲和键包含租户边界；所有归属探测都在 Engine 侧使用已验证的用户上下文执行。Provider 对无权访问、跨租户或已删除会话统一返回未找到语义。会话同时命中多个 Provider 时返回冲突，不自动选取。

## Provider contract

第三方接入实现 `IAgentProviderFactory` 和 `IAgentProvider`，并注册 Factory。`AgentProviderRequestContext` 只包含已认证用户上下文和当前请求认证令牌，不传递 `HttpContext`；租户从 `IAgentUserContext.TenantId` 获取。内置 Engine Provider 原样透传当前请求的 `Authorization`；Router 与 Engine 使用同一套认证配置和认证管线，用户和租户身份不通过自定义 header 传输。

- `GetAgentsAsync`：返回 `AgentProviderCatalog`；`IsAvailable=false` 表示不能形成完整目录。
- `ResolveConversationAsync`：返回 `NotFound`、`Found`、`Forbidden` 或 `Unavailable`。
- `RecognizeIntentAsync`：调用指定意图 Agent，返回标准化选择结果。
- `ResolveForwardingAsync`：按 action、租户与会话解析聊天目标。
- `ConfigureRequestAsync`：在 YARP 转发前处理 Provider 认证或协议适配。

内置 Engine Provider 使用 `GET /api/v1/agent/provider/conversations/{conversationId}` 探测归属。请求复用当前调用方的 `Authorization` 令牌；端点从 Engine 已验证的 `IAgentUserContext` 获取用户和租户，只返回 204/404，不返回会话内容。
意图识别使用 `POST /api/v1/agent/chat/intent`，只执行一次性 Agent 调用，不创建会话；该调用使用 Provider 配置的服务凭据，不转发渠道用户上下文。

## 配置

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
            "ConversationPath": "/api/v1/agent/provider/conversations",
            "ServiceHeaders": { "Authorization": "use-a-secret-provider" }
          }
        },
        {
          "Id": "partner-a",
          "Type": "OpenAgentEngine",
          "Settings": {
            "BaseUrl": "https://partner.example",
            "ServiceHeaders": { "Authorization": "use-a-secret-provider" }
          }
        }
      ]
    }
  }
}
```

`BaseUrl` 存在时 Provider 直接使用该地址；否则内置 Provider 使用 `IRouteTable` 发现 Engine 实例。`Settings` 对 Registry 保持不透明，敏感凭据应由环境变量或密钥配置源注入。`DefaultProviderId` 仍是必填的注册表兼容配置，但不会覆盖显式 Agent 映射或会话亲和。Fallback Agent 也必须存在于完整、已授权的目录中。

实际渠道会话统一使用 `ConversationType.Channel`，具体渠道通过已有的 `ClientType` 区分；意图识别使用无会话执行，不携带会话分类字段。

## 错误语义

| HTTP / code | 含义 | 回退 |
|-------------|------|------|
| `404 agent_not_found` | Agent 不存在或当前用户不可见 | 无 |
| `409 agent_id_conflict` | 可见目录存在重复公开 ID | 无 |
| `409 conversation_owner_conflict` | 多个 Provider 声明同一会话 | 无 |
| `409 conversation_provider_mismatch` | 显式 Agent 与已绑定会话不在同一 Provider | 无 |
| `404 conversation_not_found` | 会话不存在、已删除或用户无权访问 | 无 |
| `503 agent_provider_unavailable` | 已知 Provider 或完整目录不可用 | 不跨 Provider 回退 |
| `503 conversation_owner_unresolved` | 无亲和且迁移探测不完整 | 不落到默认 Provider |
| `503 no_agent_available` | 意图和授权后的 fallback 均无法选择 Agent | 无 |

关键实现位于 `Backend/src/OpenAgent.Router/Routing/AgentCatalogService.cs`、`ConversationProviderResolver.cs` 与 `Backend/src/OpenAgent.Engine.Host/Extensions/AgentProviderEndpointExtensions.cs`。
