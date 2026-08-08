# S3 兼容附件对象存储

Engine 的 multipart 附件可选择写入 S3 或兼容 S3 API 的对象存储。对象存储关闭时保持原有行为：附件只存在于当前请求内存中；开启后，原始字节在交给模型的同时写入私有 Bucket，会话消息只保存对象引用和摘要，不保存原始字节。

## 数据流

```text
multipart request
  -> AgentAttachmentReader（数量、大小、扩展名、MIME 校验）
  -> SHA-256
  -> IAttachmentObjectStore
       |-> disabled: NullAttachmentObjectStore
       `-> enabled: S3AttachmentObjectStore -> AWS S3 / MinIO
  -> AgentAttachment（内存字节 + ObjectKey + Sha256）
  -> MAF model input
  -> ConversationMessage.Metadata（文件名、MIME、长度、ObjectKey、Sha256）
```

对象上传发生在模型执行之前。一次请求包含多个文件时，如果后续文件校验或上传失败，Reader 会尽力删除本次已经写入的对象，然后保留原始异常。模型执行开始后的失败仍按失败会话保留附件引用；长期清理由 Bucket 生命周期策略负责。

## 配置

```json
{
  "Attachments": {
    "ObjectStorage": {
      "Enabled": true,
      "BucketName": "openagent-attachments",
      "KeyPrefix": "attachments",
      "Region": "us-east-1",
      "ServiceUrl": "http://localhost:9000",
      "ForcePathStyle": true
    }
  }
}
```

凭据不要写入 `appsettings.json`。MinIO 本地联调可以使用：

```bash
export Attachments__ObjectStorage__AccessKey="$MINIO_ROOT_USER"
export Attachments__ObjectStorage__SecretKey="$MINIO_ROOT_PASSWORD"
```

连接 AWS S3 时省略 `ServiceUrl`，通常也保持 `ForcePathStyle=false`。未显式配置 `AccessKey` 与 `SecretKey` 时，AWS SDK 使用其标准凭据解析链，适合 IAM Role、容器凭据与开发者 profile。两项静态凭据必须同时提供；无效 Bucket、Region 或 endpoint 会在应用启动验证阶段失败。

## 对象键与安全边界

对象键格式：

```text
{KeyPrefix}/{tenant-sha256-prefix}/{yyyy/MM/dd}/{opaque-guid}.{extension}
```

- 对象键不包含原始租户 ID、用户 ID、会话 ID 或文件名；
- 对象存储开启时缺少租户上下文会拒绝请求，不写入共享的 `unscoped` 分区；
- 原始文件名只进入受控会话 metadata，不作为对象路径；
- Bucket 必须保持 private，代码不生成公开 URL 或 presigned URL；
- `ObjectKey` 只是定位符，不是授权令牌；未来读取/下载端点仍必须重新校验租户与会话权限；
- 原始字节、静态凭据和授权 Header 不写日志；
- `attachment-object-storage` readiness check 会验证 Bucket 可访问性。

## 本地 MinIO

仓库提供仅用于本地联调的 Compose：

```bash
cd deploy/minio
cp .env.example .env
# 修改 .env 中的密码
docker compose up -d
```

该 Compose 会创建 private 的 `openagent-attachments` Bucket。MinIO 镜像固定到公开容器仓库可用的版本，仅作为本地 S3 兼容性工具；生产环境应使用组织批准且持续维护的 S3 服务，不应直接复制本地 root 凭据或单节点部署。

## 生命周期与权限

生产 Bucket 建议：

- Engine 运行身份只授予指定 Bucket/Prefix 的 PutObject、DeleteObject 与 readiness 所需 Bucket 查询权限；
- 禁止匿名访问和公共 ACL；
- 开启服务端加密、版本策略与审计日志；
- 按产品保留期配置生命周期删除，避免失败会话或客户端中断产生的长期孤儿对象；
- 数据库会话 metadata 是附件业务归属记录，对象存储是原始字节的唯一事实源，不能反向扫描 Bucket 推断租户权限。

## 失败语义

| 情况 | 行为 |
|------|------|
| 对象存储关闭 | 请求继续执行，`ObjectKey` 为空 |
| 配置无效 | Engine 启动失败 |
| Bucket/网络/凭据错误 | multipart 请求返回依赖不可用错误 |
| 同请求后续附件失败 | 尽力回滚本请求已上传对象 |
| 回滚删除失败 | 记录 ObjectKey 和异常类型，不覆盖原始请求错误 |

## 已验证范围

2026-08-08 在本地完成以下验证：

- Release 全解决方案构建为 0 warning / 0 error；
- 后端 179 个测试通过，包括配置验证、disabled fallback、上传参数、租户缺失拒绝、租户分区、SDK 错误映射、删除、readiness 与多文件回滚；
- `docker compose config` 通过，MinIO 启动并自动创建 private Bucket；
- Engine `/ready` 中 `attachment-object-storage` 为 Healthy；
- multipart 请求经 Engine 调用模拟 OpenAI Chat Completions 后返回 200；
- MinIO 中对象大小、MIME、SHA-256 与租户哈希 metadata 正确；
- 会话详情仅保存 `FileName`、`MediaType`、`Length`、`ObjectKey`、`Sha256`，不包含附件字节；
- 联调使用的容器、网络与 volume 已清理。
