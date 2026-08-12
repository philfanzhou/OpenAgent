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
- 文件原始字节只在对象存储中；PostgreSQL 保存资产元数据、会话和引用关系。
- 同一资产可被多条消息引用。会话追加通过 `Version` 并发令牌和 `expectedVersion` 防止写覆盖。
- 删除会话为软删除，不删除用户资产或对象存储中的原始文件。

## Engine 协调

Engine 的服务注册、心跳、配置热更新和能力发现仍可使用 Redis 作为可选的短生命周期协调设施。
它不参与会话或文件资产的持久化；Redis 不可用时 Engine 以现有孤岛模式降级。
