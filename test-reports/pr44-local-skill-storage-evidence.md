# PR44 实际运行验收

验证基线：`cfa98ed95bf449eb357dbb70c014ed0a256be49a`

## 真实页面/API 场景

在当前 worktree 构建并启动的 Engine（`http://localhost:55208`）上，用 Basic 认证上传 `e2e-skill.md`：

```text
POST /api/v1/admin/skills/packages -> 200
tenantId: development
skillId: minio-tenant-e2e
type: AgentSkill
sourceType: ObjectStorage
packageFormat: directory
storage: object-storage-directory
```

随后读取：

```text
GET /api/v1/admin/skills/minio-tenant-e2e -> 200
GET /api/v1/admin/skills/minio-tenant-e2e/source -> 200
```

返回的 `objectKey` 位于：

```text
files/tenants/875b9380866e9d56e7110b0ee310962c16d9d4ae103f829d62bdffd2cbe7c61d/users/8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918/skill-packages/skill-b699b83d2b89460ead7afe1767712b9d/skill-b699b83d2b89460ead7afe1767712b9d.json
```

## MinIO 实际对象

MinIO `openagent-files` 中同一租户/用户/包目录实际列出：

```text
SKILL.md       171B
skill-b699b83d2b89460ead7afe1767712b9d.json  379B
```

索引对象内容确认 `TenantId=development`，并记录 `SKILL.md` 的对象键和 SHA-256。

## PostgreSQL 实际记录

`openagent.skill_definitions` 实际存在一行：

```text
TenantId=development
SkillId=minio-tenant-e2e
Type=AgentSkill
SourceType=ObjectStorage
```

## HTTP Skill 移除

对原 HTTP Skill 更新入口实发请求：

```text
PUT /api/v1/admin/skills/http -> 405
```

结论：PR44 已验证“Skill 元数据进 PostgreSQL、Skill 文件进租户对象存储、索引可回读”，Redis 不是事实源；HTTP Skill 管理入口已不存在。
