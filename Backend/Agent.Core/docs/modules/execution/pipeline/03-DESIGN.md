# Pipeline — 设计

`Pipeline` 只构建入口 middleware 委托链，并把核心执行直接交给 `AgentRun`。

```text
AgentRequest
  -> AgentIdValidation
  -> TenantValidation
  -> Tracing
  -> Auth
  -> AuditLogging
  -> AgentRun.Run[Streaming]Async
```

非流式路径把异常映射为 `AgentResponse`；流式路径保留异常，由 Host 的流协议边界映射。
不得在 Pipeline 与 AgentRun 之间增加 Service facade。
