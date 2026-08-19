# PR46 实际运行验收

验证基线：PR46 已基于 PR44 `cfa98ed95bf449eb357dbb70c014ed0a256be49a` 重放，当前安全修改尚未推送。

## Header 伪造场景

对同一个已认证 Skill 使用冲突租户 Header，Engine 业务边界拒绝：

```text
GET /api/v1/admin/skills/minio-tenant-e2e
Authorization: Basic ...
X-Tenant-Id: foreign-tenant
-> 403
```

完整 Router → Engine 链路同样返回 403；Router 自身不解释、不清理该 Header，只原样转发：

```text
POST /api/v1/agent/chat
Authorization: Basic ...
X-Tenant-Id: foreign-tenant
-> 403
```

Header-only 的租户身份在 Engine 中不再建立租户上下文；Router 只转发原始请求，不管理或移除 `X-Tenant-Id`、`X-TenantId` 等业务 Header，并保留认证凭据供下游重新认证。

## 自动化结果

```text
dotnet test Backend/OpenAgent.sln
Contracts       4/4
Infrastructure  7/7
Hosting        21/21
Core           77/77
Engine         79/79
Router        121/121
Architecture    6/6
总计          315/315

pnpm test -- --run
6 test files, 44/44 passed
```

另外修复了真实容器启动时发现的生命周期问题：会话存储依赖请求级 `ICurrentUserContext`，现改为 Scoped；当前 PR46 Engine 容器健康检查通过。
