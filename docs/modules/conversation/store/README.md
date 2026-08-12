# 会话持久化

`IConversationStore` 的生产实现是 `OpenAgent.Persistence.PostgresConversationStore`。每次请求从 PostgreSQL 读取会话历史，以 `Version` 进行乐观并发追加，并在同一持久化操作中保存会话、消息和消息级文件引用。

`IConversationLock` 默认仅提供进程内串行化；它不承担数据存储职责。分布式协调可以在未来作为可选适配器引入，但不能改变 PostgreSQL 作为会话事实源。
