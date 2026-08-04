# Streaming — 设计

```text
Pipeline.ExecuteStreamAsync
  -> AgentRun.RunStreamingAsync
  -> ChatClientAgent.RunStreamingAsync
  -> AgentResponseUpdate + MafResponseReader
  -> assistant chunks / tool markers / usage
```

函数迭代由 MAF 管理。`PlatformChatHistory` 接收 MAF 的完成/失败通知，写回最终或
partial assistant，并释放会话锁。平台流边界只投影文本、工具提示和 usage。
