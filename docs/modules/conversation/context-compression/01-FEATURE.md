# Context Compression

上下文压缩由 Microsoft Agent Framework `CompactionProvider` 执行，不再维护平台压缩器。

支持的 `ContextPolicy.Strategy`：

- `sliding_window` → `SlidingWindowCompactionStrategy`
- `summarize` → `SummarizationCompactionStrategy`
- `none` / 未配置 → 按平台历史上限使用 `TruncationCompactionStrategy`

压缩发生在 MAF 模型调用前，保持函数调用与函数结果的原子消息组，并可覆盖同一 Agent
run 内的后续工具迭代。
