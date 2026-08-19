# PR44 实际运行验收：租户共享 Skill 对象存储

验证代码：当前工作树 `fdf3ac2`；原 Compose 项目 `agentmatrix`，原容器已使用当前工作树重新构建并替换。

## 真实页面/API 场景

在原 agentmatrix Engine（`http://localhost:5208`）上，用 Basic 认证上传 `e2e-skill-original-docker.md`：

```text
POST /api/v1/admin/skills/packages -> 200
tenantId: development
skillId: original-docker-proof
type: AgentSkill
sourceType: ObjectStorage
packageFormat: directory
storage: object-storage-directory
```

随后读取：

```text
GET /api/v1/admin/skills/tenant-shared-proof/source -> 200
```

返回的 `objectKey` 位于：

```text
files/tenants/875b9380866e9d56e7110b0ee310962c16d9d4ae103f829d62bdffd2cbe7c61d/skill-packages/skill-b799c0e3d33d463b94f5849fd8d14fd6/skill-b799c0e3d33d463b94f5849fd8d14fd6.json

关键验证：该键包含租户哈希和 `skill-packages`，不包含 `/users/` 或上传用户哈希。
```

## MinIO 实际对象

MinIO `openagent-files` 中同一租户/包目录实际列出：

```text
SKILL.md       213B
skill-b799c0e3d33d463b94f5849fd8d14fd6.json  308B
```

截图：[原 agentmatrix MinIO 租户共享 Skill 目录](/Users/zhuzy/.codex/worktrees/351e/Agent.Matrix/test-reports/screenshots/pr44-original-docker-minio.png)

索引对象内容确认 `TenantId=development`，并记录 `SKILL.md` 的对象键和 SHA-256；MinIO 实际列表没有 `users` 层级。

## PostgreSQL 实际记录

`openagent.skill_definitions` 实际存在一行：

```text
TenantId=development
SkillId=original-docker-proof
Type=AgentSkill
SourceType=ObjectStorage

`DefinitionJson.ObjectKey` 与接口和 MinIO 列表中的租户共享键一致。
```

## HTTP Skill 移除

## 自动化结果

```text
docker compose -p agentmatrix up -d --build -> Engine/Router/Chat rebuilt and replaced
Engine health -> Healthy
Router health -> Healthy
Chat page -> HTTP 200
Engine focused tests -> 10/10 passed
Full backend test suite -> 316/316 passed
```

结论：PR44 已验证“Skill 元数据进 PostgreSQL、Skill 文件实际进租户共享对象存储、对象键不含 user 分区、索引可回读”。普通用户文件仍可使用 `tenant/user` 分区；Skill 不再复用该路径。
