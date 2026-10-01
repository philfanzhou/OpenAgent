# conversation — 会话记录与存储

| 功能点 | 说明 | 文档 |
|--------|------|------|
| store | PostgreSQL 会话、消息和文件引用存储 | [store/](./store/) |
| context-compression | MAF CompactionProvider 摘要压缩 | [context-compression/](./context-compression/) |

## 历史 Provider 内部职责

`PlatformChatHistory` 保留会话锁、轮次暂存、成功/失败/取消落库和释放兜底。
`HistoryAttachments` 负责文件清单、图片数量/字节窗口及内联读取；`ToolHistoryRepair`
负责工具调用组拼接、去重与孤立调用剔除。存储消息初始化复用 `ConversationSessionStore.Message`。

自动与手动压缩通过 `ConversationHistoryFactory.CreateAuditedStrategy` 创建同一策略组合。
手动压缩保留归属检查、会话锁、强制记录和 `Manual` 审计触发；自动路径保留 `Automatic` 触发。
