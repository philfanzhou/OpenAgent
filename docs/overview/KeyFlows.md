# Key Flows — OpenAgent

## 会话与文件请求

```text
Browser
  -> POST /files
  -> PostgreSQL file_assets (Pending)
  -> S3-compatible object storage
  -> PostgreSQL file_assets (Ready)

Browser
  -> POST /chat/stream { conversationId, fileIds }
  -> FileAssetService reads ready assets
  -> Agent runtime and model
  -> PostgreSQL conversations + conversation_messages
       + conversation_file_references + message_file_references
  -> SSE response
```

- 文件先作为用户资产创建；发送聊天消息时仅提交 `fileIds`。
- 跨系统发送文件时调用文件资产 `transfer-url` 端点，第三方使用短期签名 URL 读取实际 S3 `ObjectKey` 对应的对象。
- 文件原始字节只在对象存储中；PostgreSQL 保存资产元数据、会话和引用关系。
- 同一资产可被多条消息引用。会话追加通过 `Version` 并发令牌和 `expectedVersion` 防止写覆盖。
- 删除会话为软删除，不删除用户资产或对象存储中的原始文件。

## Engine 协调

Engine 的服务注册、心跳、配置热更新和能力发现使用 Redis 作为可选协调设施。Redis 还可以保存会话
热副本并提供分布式会话锁：写入顺序为 PostgreSQL 成功后写 Redis，读取未命中从 PostgreSQL 回填；
它不拥有会话或文件资产事实。未配置 Redis 的单实例环境使用本地锁且直接读取数据库。

## Router 多 Provider 路由

Router 聚合并授权过滤各 Provider 的 Agent 目录，公开响应不暴露 Provider 标识。新会话把公开 Agent ID 解析为内部 Provider 并写入租户隔离的亲和记录；旧会话通过 Provider 所有权探测回填归属。续聊始终使用已确认归属，Provider 不可用或归属冲突时明确失败，不跨 Provider 静默回退。协议和错误语义见 [Agent Provider 集成](../integrations/agent-provider.md)。
