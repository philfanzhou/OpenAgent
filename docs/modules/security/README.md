# security — 安全与租户

security 域负责 Agent Pipeline 的安全关卡，包括认证、租户隔离、AgentId 可观测性及权限评估。

## 功能点

| 功能点 | 说明 | 文档 |
|--------|------|------|
| auth | JWT Bearer 认证 | [auth/](./auth/) |
| tenant | 租户隔离与校验 | [tenant/](./tenant/) |
| agent-id-validation | AgentId 前置校验 | [agent-id-validation/](./agent-id-validation/) |
| permission | 权限评估与 ACL | [permission/](./permission/) |
