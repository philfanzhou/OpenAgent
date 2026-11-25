# Intern Decision 路由接口

Router 向 `POST /v1/route` 发送 JSON。请求包含用户本轮的 `query`、映射为 `{Agent ID: 说明}` 的 `agents`，以及其中一个 Agent ID 作为 `fallback`。候选经过平台授权，最多 62 个。授权候选中存在已配置的 fallback 时使用它，否则选第一个候选。服务密钥通过 `OPENAGENT_INTERN_DECISION_API_KEY` 提供，放在请求的 `X-API-Key` 头。

响应包含 `agent`、`confidence` 和 `action`。`execute` 与 `review` 可进入 Router 的候选与置信度复核。`fallback`、无有效决策、网络故障或超时时，Router 继续执行配置的授权回退规则。HTTP 请求不走系统代理，也不跟随重定向。HTTP 失败日志保留状态码和 Provider ID。

调用时限使用 `RouterSettings:IntentRecognition:TimeoutMs`，默认 5000 毫秒。连续 5 次请求失败后暂停调用 30 秒，冷却后允许一个探测请求，通过后恢复。调用人取消会传递到下游。部署新服务地址时先确保 `RouterSettings:AgentProviders:Providers:<索引>:Settings:BaseUrl` 与现网配置一致，再验证 Auto 实际选路和降级行为。

本地合同测试由 `OPENAGENT_INTERN_DECISION_TEST_URL` 启用，缺少测试服务会显示跳过。部署核对见 [部署指南](../operations/deployment.md)。
