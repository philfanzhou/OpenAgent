# External Agent 集成

Router 可以把 Engine Agent 与外部 Agent 合并为一个可路由目录。未显式指定 `agentId` 时，意图 Agent 只会看到当前用户有权访问的候选项；选中外部 Agent 后，请求由对应 Adapter 转换并直接转发，不再进入 Engine 执行链。

## 配置

外部 Agent 位于 `RouterSettings:ExternalAgents:Agents`。生产环境应通过环境变量或密钥配置源注入 `Authentication:Token`，不要将凭据提交到配置文件。

```json
{
  "AgentId": "external-support",
  "Name": "Partner Support",
  "Description": "Handles partner support requests",
  "Adapter": "OpenAgent",
  "BaseUrl": "https://partner.example",
  "ChatPath": "/api/v1/agent/chat",
  "RemoteAgentId": "support-v2",
  "ForwardIdentityHeaders": false,
  "Authentication": {
    "HeaderName": "Authorization",
    "Scheme": "Bearer",
    "Token": ""
  }
}
```

`AgentId` 是 Router 内的全局路由 ID；外部配置与 Engine Agent 同名时，显式外部配置优先。`RemoteAgentId` 是第三方服务实际接收的 Agent ID。当前内置 `OpenAgent` Adapter 支持普通请求、SSE、附件请求及自定义聊天路径。

## 扩展与安全边界

- 外部配置由 `IExternalAgentRegistry` 提供，目录合并由 `IAgentCatalog` 负责。
- 请求格式、目标路径和认证头由 `IExternalAgentAdapter` 处理；接入其他协议时新增实现并通过 DI 注册，不需要修改意图选择器。
- Router 仅保留内容头与 `Accept`，会移除客户端的 Cookie、认证头、身份头及其他请求头，再写入受信任配置。用户凭据不会传给第三方。
- `ForwardIdentityHeaders` 默认为 `false`。只有第三方处于受信任边界并确实需要用户/租户头时才应启用。
- 外部 Agent 仍使用 Router 的 Agent ACL 检查；统一网关权限策略上线后，该目录会直接消费网关裁剪后的候选项。

关键实现位于 `Backend/src/OpenAgent.Router/Routing/AgentCatalog.cs`、`Backend/src/OpenAgent.Router/Endpoints/ExternalAgentForwarder.cs` 和 `Backend/src/OpenAgent.Router/Endpoints/OpenAgentExternalAdapter.cs`。
