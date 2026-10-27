# 能力注册（Engine Runtime）

LLM 不再通过启动期 Registrar 或内存 Registry 注册，而是在每次执行时按租户和 `llmProfileId` 从
PostgreSQL/Redis TTL 缓存解析；Agent、Runtime 与 MCP 配置同样以 PostgreSQL 为事实源。

```text
AgentRuntimeResolver    → Agent + LLM Profile + 授权 → AgentRuntimeProfile
McpFeatureFactory       → RuntimeMcpServer[]（ACP 透传）
SkillFeatureFactory     → Skill 包定义（物化到本轮工作区）
ICapabilitySource       → 平台自有工具（RuntimeTool 回调）
```

平台自有工具经 `CapabilityToolFactory` 筛选转换为驱动可调用工具；外部 MCP 与 Skill 不再创建
官方 SDK 客户端资源，分别经 ACP 透传与工作区物化交给 harness 原生处理。RAG 目录仍在
Engine/Redis（`RedisRagRegistrar`）。
