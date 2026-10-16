# Agent Provider 集成

Router 通过 `IAgentProvider` 调用所有下游，只有一种实现 `HttpProvider`：下游必须提供[下游契约 v1](../modules/router/downstream-contract.md)规定的标准接口，Router 不为单个下游做协议适配，也不再配置接口路径。Provider 负责目录读取、会话归属探测、意图调用和目标解析；Router 负责公开目录、Provider 映射、会话亲和与统一转发。

## 公开 Agent Catalog

`GET /api/v1/agent/agents`（兼容别名：`GET /api/v1/agents`）会读取所有 Provider 的目录并合并。响应仍为按 `agentId` 排序的 `AgentSummary[]`，不会包含 `providerId`、内部地址或 Provider 配置。

公开 `agentId` 保留 Provider 发布的稳定 ID，Router 只在内部维护其 Provider 映射。同一用户可见范围内若两个 Provider 发布相同 `agentId`，目录和显式选择均返回 `409 agent_id_conflict`，不会用 Provider 前缀改写 ID 或按注册顺序静默选取。

显式 `agentId` 必须先在完整目录中找到，再定位 Provider。目录由下游按调用者过滤，Router 不再做授权过滤；不存在或下游未返回的 Agent 统一返回 `404 agent_not_found`。任何 Provider 的目录不可用时返回 `503 agent_provider_unavailable`，避免在不完整快照上产生不稳定映射。

## 会话归属与亲和

亲和记录以租户和 `conversationId` 的摘要为键，值只保存内部 `providerId` 与 `Pending`/`Confirmed` 状态；配置 Redis 时由分布式缓存持久化，未配置时使用进程内分布式缓存实现。

1. 新会话完成显式或自动选择后写入 `Pending` 亲和，重试仍固定到同一 Provider。
2. 续聊先读取亲和，再通过 Provider contract 验证归属；首次验证成功后转为 `Confirmed`。
3. 旧会话没有亲和时逐一探测所有 Provider；唯一命中会回填 `Confirmed`，实现无停机迁移。
4. 已确认会话在原 Provider 不存在时重新探测其他 Provider；唯一命中会更新归属。
5. 已绑定 Provider 不可用、迁移探测不完整或 Provider 已移除时不回退到默认 Provider，避免拆分会话历史。

亲和键包含租户边界；所有归属探测都在 Engine 侧使用已验证的用户上下文执行。Provider 对无权访问、跨租户或已删除会话统一返回未找到语义。会话同时命中多个 Provider 时返回冲突，不自动选取。

## Provider contract

`AgentProviderRequestContext` 只包含已认证用户上下文和当前请求认证令牌，不传递 `HttpContext`；租户从 `IAgentUserContext.TenantId` 获取。`HttpProvider` 原样透传当前请求的 `Authorization`，下游自行验证，用户和租户身份不通过自定义 header 传输。

- `GetAgentsAsync`：`GET /api/v1/agent/agents`，返回 `AgentProviderCatalog`；`IsAvailable=false` 表示不能形成完整目录。
- `ResolveConversationAsync`：`GET /api/v1/agent/provider/conversations/{id}`，204 为找到，404 为未找到，401/403 为无权，其余视为不可用。请求复用调用方的 `Authorization`，只返回状态码，不返回会话内容。
- `RecognizeIntentAsync`：`POST /api/v1/agent/chat/intent` 调用指定意图 Agent，返回标准化选择结果；该调用使用 Provider 配置的服务凭据，不转发渠道用户上下文。默认生成并持久化普通会话（Engine 入口的 `PersistIntentConversations` 开关可切回无会话执行）。
- `ResolveForwardingAsync`：按 action、租户与会话解析聊天目标，转发到 `/api/v1/agent/chat[/stream]`。

路径固定在 `Router.Contracts` 的 `DownstreamPaths` 中，下游不能自定义。接入新下游的方式是让它实现标准接口，而不是在 Router 里增加适配代码。

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
        { "Id": "self-engine" },
        {
          "Id": "partner-a",
          "BaseUrl": "https://partner.example",
          "ServiceHeaders": { "Authorization": "use-a-secret-provider" }
        }
      ]
    }
  }
}
```

没有 `BaseUrl` 的 Provider 通过 `IRouteTable` 发现 Engine 实例；配置了 `BaseUrl` 时直接使用该地址，必须是 http 或 https 绝对地址。`ServiceHeaders` 只用于意图识别等服务调用，敏感凭据应由环境变量或密钥配置源注入。`DefaultProviderId` 仍是必填的注册表兼容配置，但不会覆盖显式 Agent 映射或会话亲和。Fallback Agent 也必须存在于完整目录中。

**破坏性变更：** 旧配置中的 `Type`、`Settings`（`AgentListPath`、`ChatPath`、`ConversationPath`、`BaseUrl`、`ServiceHeaders`）已移除，`BaseUrl` 与 `ServiceHeaders` 上移到 Provider 项本身；`Gina` 类型 Provider 已删除，Gina 需自行实现标准接口后以普通 Provider 接入。

实际渠道会话统一使用 `ConversationType.Channel`，具体渠道通过已有的 `ClientType` 区分；意图识别的会话分类和会话 ID 由 Engine 入口内部生成，不由 Provider 请求携带。

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

关键实现位于 `Backend/src/OpenAgent.Router.Core/Routing/AgentCatalogService.cs`、`ConversationProviderResolver.cs` 与 `Backend/src/OpenAgent.Engine.Host/Extensions/AgentProviderEndpointExtensions.cs`。
